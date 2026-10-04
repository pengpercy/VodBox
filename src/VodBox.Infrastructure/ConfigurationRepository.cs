using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VodBox.Core;

namespace VodBox.Infrastructure;

/// <summary>Stores resolved snapshots, retaining the original location for explicit refresh.</summary>
public sealed class ConfigurationRepository(string directory)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string IndexPath => Path.Combine(directory, "index.json");
    private string SnapshotPath(string id) => Path.Combine(directory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))) + ".json");
    public async Task<IReadOnlyList<SavedConfiguration>> ListAsync(CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { return await ReadIndexAsync(token); }
        finally { _gate.Release(); }
    }
    private async Task<List<SavedConfiguration>> ReadIndexAsync(CancellationToken token) => File.Exists(IndexPath)
        ? JsonSerializer.Deserialize(await File.ReadAllTextAsync(IndexPath, token), VodBoxJson.Default.ListSavedConfiguration) ?? [] : [];
    public async Task SaveAsync(VodBoxConfig resolved, string location, CancellationToken token = default)
    {
        ConfigLoader.Validate(resolved);
        await _gate.WaitAsync(token);
        try
        {
            await AtomicFile.WriteAsync(SnapshotPath(resolved.Id), JsonSerializer.Serialize(resolved, VodBoxJson.Default.VodBoxConfig), token);
            var index = await ReadIndexAsync(token); index.RemoveAll(x => x.Id == resolved.Id);
            index.Add(new(resolved.Id, location, DateTimeOffset.UtcNow));
            await AtomicFile.WriteAsync(IndexPath, JsonSerializer.Serialize(index, VodBoxJson.Default.ListSavedConfiguration), token);
        }
        finally { _gate.Release(); }
    }
    public async Task<VodBoxConfig> LoadAsync(string id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var config = JsonSerializer.Deserialize(await File.ReadAllTextAsync(SnapshotPath(id), token), VodBoxJson.Default.VodBoxConfig)
                ?? throw new InvalidDataException("保存的配置为空。");
            ConfigLoader.Validate(config); return config;
        }
        finally { _gate.Release(); }
    }
    public async Task RemoveAsync(string id, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try
        {
            var index = await ReadIndexAsync(token); index.RemoveAll(x => x.Id == id);
            await AtomicFile.WriteAsync(IndexPath, JsonSerializer.Serialize(index, VodBoxJson.Default.ListSavedConfiguration), token);
            File.Delete(SnapshotPath(id));
        }
        finally { _gate.Release(); }
    }
}

public sealed class PreferencesStore(string path)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    public async Task<AppPreferences> LoadAsync(CancellationToken token = default)
    {
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize(await File.ReadAllTextAsync(path, token), VodBoxJson.Default.AppPreferences) ?? new();
    }
    public async Task SaveAsync(AppPreferences preferences, CancellationToken token = default)
    {
        await _gate.WaitAsync(token);
        try { await AtomicFile.WriteAsync(path, JsonSerializer.Serialize(preferences, VodBoxJson.Default.AppPreferences), token); }
        finally { _gate.Release(); }
    }
}

internal static class AtomicFile
{
    public static async Task WriteAsync(string path, string text, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temporary, text, token); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { File.Delete(temporary); }
    }
}
