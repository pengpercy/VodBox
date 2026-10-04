using System.Runtime.CompilerServices;
using System.Threading.Channels;
using VodBox.Core;

namespace VodBox.Application;

public sealed class AggregateSearch(IProviderFactory factory)
{
    public async IAsyncEnumerable<SearchBatch> SearchAsync(IReadOnlyList<SourceDefinition> sources, string query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(4, 4);
        var output = Channel.CreateBounded<SearchBatch>(8);
        var workers = sources.Select(async source =>
        {
            await slots.WaitAsync(cancellation.Token);
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(12));
                SearchBatch batch;
                try
                {
                    await using var provider = factory.Create(source);
                    var result = await provider.SearchAsync(query, timeout.Token);
                    batch = new(source, result.Items.Select(x => new SearchHit(source, x)).ToList());
                }
                catch (Exception ex) when (!cancellation.IsCancellationRequested)
                { batch = new(source, [], ex is OperationCanceledException ? "搜索超时" : ex.Message); }
                await output.Writer.WriteAsync(batch, cancellation.Token);
            }
            finally { slots.Release(); }
        }).ToArray();
        var producer = CompleteAsync();
        async Task CompleteAsync()
        {
            try { await Task.WhenAll(workers); output.Writer.TryComplete(); }
            catch (Exception ex) { output.Writer.TryComplete(ex); }
        }
        try { await foreach (var batch in output.Reader.ReadAllAsync(cancellationToken)) yield return batch; }
        finally { cancellation.Cancel(); await producer; }
    }
}
