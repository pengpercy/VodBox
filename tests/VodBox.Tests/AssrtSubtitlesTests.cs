using System.Text;
using VodBox.Infrastructure;
using Xunit;

namespace VodBox.Tests;

public sealed class AssrtSubtitlesTests
{
    [Fact]
    public async Task SearchUsesHeaderCredentialAndEncodedKeyword()
    {
        string? address=null;IReadOnlyDictionary<string,string>? headers=null;
        var provider=new AssrtSubtitles((url,values,_)=>{address=url;headers=values;return Task.FromResult(Encoding.UTF8.GetBytes("{\"status\":0,\"sub\":{\"subs\":[{\"id\":12,\"native_name\":\"字幕名称\"}]}}"));});
        var result=await provider.SearchAsync("片名 & 第二季","test-credential",TestContext.Current.CancellationToken);
        Assert.Equal("12",Assert.Single(result).Id);Assert.Contains("%26",address);Assert.DoesNotContain("test-credential",address);
        Assert.Equal("Bearer test-credential",headers!["Authorization"]);
    }

    [Fact]
    public async Task MalformedSubtitleResponseDoesNotTreatUnexpectedRootAsResults()
    {
        var provider=new AssrtSubtitles((_,_,_)=>Task.FromResult(Encoding.UTF8.GetBytes("[]")));
        await Assert.ThrowsAsync<InvalidDataException>(()=>provider.SearchAsync("movie","test-credential",TestContext.Current.CancellationToken));
        var invalid=new AssrtSubtitles((_,_,_)=>Task.FromResult(Encoding.UTF8.GetBytes("{\"sub\":null}")));
        Assert.Empty(await invalid.SearchAsync("movie","test-credential",TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DetailFiltersArchivesAndNonHttpsDownloads()
    {
        var provider=new AssrtSubtitles((_,_,_)=>Task.FromResult(Encoding.UTF8.GetBytes("{\"status\":0,\"sub\":{\"subs\":[{\"filelist\":[{\"f\":\"movie.srt\",\"url\":\"https://files.example/movie.srt\"},{\"f\":\"archive.zip\",\"url\":\"https://files.example/archive.zip\"},{\"f\":\"bad.srt\",\"url\":\"file:///secret\"}]}]}}")));
        Assert.Equal("movie.srt",Assert.Single(await provider.FilesAsync("12","test-credential",TestContext.Current.CancellationToken)).Name);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>provider.SearchAsync("movie","",TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidDataException>(()=>provider.FilesAsync("../secret","test-credential",TestContext.Current.CancellationToken));
    }
}
