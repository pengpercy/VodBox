using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VodBox.Desktop.Services;

public sealed record UpdateOffer(string Version, string Name, Uri Url, string Sha256, string ReleaseUrl);

/// <summary>Release discovery, verified staging and detached replacement. No replacement occurs before explicit UI consent.</summary>
public static class AppUpdater
{
    private const long MaxArchiveBytes = 1_500_000_000;
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(10) };
    private static string Rid => OperatingSystem.IsWindows() ? RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64"
        : OperatingSystem.IsMacOS() ? RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64"
        : OperatingSystem.IsLinux() ? RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64" : "";

    internal static string? LinuxPackage(string osRelease)
    {
        var identifiers = osRelease.Split('\n').Where(line => line.StartsWith("ID=", StringComparison.Ordinal) || line.StartsWith("ID_LIKE=", StringComparison.Ordinal))
            .SelectMany(line => line[(line.IndexOf('=') + 1)..].Trim().Trim('"', '\'').Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (identifiers.Overlaps(["debian", "ubuntu", "linuxmint", "pop", "elementary"])) return "deb";
        if (identifiers.Overlaps(["rhel", "fedora", "centos", "rocky", "almalinux", "suse", "opensuse", "amzn"])) return "rpm";
        return null;
    }

    private static string? InstalledLinuxPackage()
    {
        var path = File.Exists("/etc/os-release") ? "/etc/os-release" : "/usr/lib/os-release";
        return File.Exists(path) ? LinuxPackage(File.ReadAllText(path)) : null;
    }


    public static bool IsPackaged(string processPath)
    {
#if DEBUG
        return false;
#else
        return OperatingSystem.IsMacOS()
        ? FindMacApp(processPath) is not null
        : OperatingSystem.IsWindows() && Path.GetFileName(processPath).Equals("VodBox.Desktop.exe", StringComparison.OrdinalIgnoreCase)
          && !processPath.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
          && !processPath.Contains(Path.DirectorySeparatorChar + "artifacts" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
#endif
    }

    public static string? FindMacApp(string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        while (dir is not null && !dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) dir = Path.GetDirectoryName(dir);
        return dir is not null && Path.GetFileName(dir) == "VodBox.app" &&
               Path.GetFullPath(path).StartsWith(Path.Combine(dir, "Contents", "MacOS") + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? dir : null;
    }

    public static UpdateOffer? Select(JsonElement release, string currentVersion, string rid, string? linuxPackage = null)
    {
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean()) return null;
        var releaseTag = release.GetProperty("tag_name").GetString();
        var tag = releaseTag?.StartsWith('v') == true ? releaseTag[1..] : releaseTag;
        if (!Regex.IsMatch(tag ?? "", @"^\d+\.\d+\.\d+(?:\.\d+)?$", RegexOptions.CultureInvariant) ||
            !Version.TryParse(tag, out var next) || !Version.TryParse(currentVersion, out var current) || next <= current) return null;
        if (rid is not ("win-x64" or "win-arm64" or "osx-x64" or "osx-arm64" or "linux-x64" or "linux-arm64")) return null;
        var linux = rid.StartsWith("linux-", StringComparison.Ordinal);
        var name = linux ? $"vodbox_{tag}_{rid}.{linuxPackage}" : $"VodBox_{tag}.{rid}.zip";
        var releaseUrl = release.GetProperty("html_url").GetString() ?? "";
        if (!ValidGithubUrl(releaseUrl, $"/pengpercy/VodBox/releases/tag/{releaseTag}")) return null;
        if (linux && linuxPackage is not ("deb" or "rpm"))
            return new UpdateOffer(tag!, "", new Uri(releaseUrl), "", releaseUrl);
        foreach (var asset in release.GetProperty("assets").EnumerateArray())
        {
            if (asset.GetProperty("name").GetString() != name) continue;
            var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() : null;
            var address = asset.GetProperty("browser_download_url").GetString();
            if (digest is null || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$") ||
                !ValidGithubUrl(address, $"/pengpercy/VodBox/releases/download/{releaseTag}/{name}")) return null;
            return new UpdateOffer(tag!, name, new Uri(address!), digest[7..].ToLowerInvariant(), releaseUrl);
        }
        return linux ? new UpdateOffer(tag!, "", new Uri(releaseUrl), "", releaseUrl) : null;
    }

    private static bool ValidGithubUrl(string? address, string expectedPath) =>
        !string.IsNullOrEmpty(address) && !address.Contains('#') && !address.Contains('@') &&
        Uri.TryCreate(address, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps &&
        url.Host == "github.com" && url.IsDefaultPort && url.UserInfo.Length == 0 &&
        url.Fragment.Length == 0 && url.Query.Length == 0 && url.AbsolutePath == expectedPath;

    public static async Task<UpdateOffer?> CheckAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/pengpercy/VodBox/releases/latest");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("VodBox", "1.0"));
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStreamAsync(cancellationToken));
        return Select(document.RootElement, currentVersion, Rid, OperatingSystem.IsLinux() ? InstalledLinuxPackage() : null);
    }

    public static async Task<string> DownloadAsync(UpdateOffer offer, string cache, CancellationToken cancellationToken, IProgress<long>? progress = null)
    {
        Directory.CreateDirectory(cache);
        // A fixed, internal filename prevents asset names from controlling filesystem paths.
        var path = Path.Combine(cache, "update.zip");
        if (File.Exists(path))
        {
            if (await MatchesAsync(path, offer.Sha256, cancellationToken)) return path;
            File.Delete(path);
        }
        var partial = Path.Combine(cache, "download.partial");
        try
        {
            using var response = await Client.GetAsync(offer.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxArchiveBytes) throw new InvalidDataException("更新包过大。");
            await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += read;
                    if (total > MaxArchiveBytes) throw new InvalidDataException("更新包过大。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report(total);
                }
            }
            if (!await MatchesAsync(partial, offer.Sha256, cancellationToken)) throw new InvalidDataException("更新包 SHA256 校验失败。");
            File.Move(partial, path, true);
            return path;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public static async Task<bool> MatchesAsync(string file, string expected, CancellationToken token = default)
    {
        if (!Regex.IsMatch(expected, "^[a-fA-F0-9]{64}$")) return false;
        await using var stream = File.OpenRead(file);
        return string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, token)), expected, StringComparison.OrdinalIgnoreCase);
    }

    public static void ExtractSafe(string archive, string destination, bool mac)
    {
        Directory.CreateDirectory(destination);
        using var zip = ZipFile.OpenRead(archive);
        long declaredSize = 0, actualSize = 0;
        var links = new List<(string Path, string Target)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName;
            if (mac && (name.StartsWith("__MACOSX/", StringComparison.Ordinal) || name == "__MACOSX/")) continue; // ditto resource fork metadata
            if (name.StartsWith('/') || name.Contains('\\') || name.Split('/').Any(p => p is ".." or "." or "") && !name.EndsWith('/'))
                throw new InvalidDataException("更新包包含非法路径。");
            var parts = name.TrimEnd('/').Split('/');
            if (parts.Any(p => p is ".." or "." or "" || p.Contains(':'))) throw new InvalidDataException("更新包包含非法路径。");
            if (mac && parts[0] != "VodBox.app") throw new InvalidDataException("更新包不包含预期 app bundle。");
            var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (!seen.Add(name.TrimEnd('/'))) throw new InvalidDataException("更新包包含重复路径。");
            if (unixType is not (0 or 0x8000 or 0x4000 or 0xA000) || (unixType == 0xA000 && !mac)) throw new InvalidDataException("更新包包含特殊文件。");
            declaredSize += entry.Length;
            if (declaredSize > 3_000_000_000 || entry.Length > MaxArchiveBytes) throw new InvalidDataException("解压内容过大。");
            var target = Path.Combine(destination, Path.Combine(parts));
            if (unixType == 0xA000)
            {
                using var linkInput = entry.Open();
                using var linkBytes = new MemoryStream();
                var linkBuffer = new byte[4096];
                int linkRead;
                while ((linkRead = linkInput.Read(linkBuffer)) != 0)
                {
                    actualSize += linkRead;
                    if (linkBytes.Length + linkRead > 4096 || actualSize > 3_000_000_000) throw new InvalidDataException("解压内容过大。");
                    linkBytes.Write(linkBuffer, 0, linkRead);
                }
                var link = System.Text.Encoding.UTF8.GetString(linkBytes.ToArray());
                var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target)!, link));
                if (Path.IsPathRooted(link) || !resolved.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
                    link.Contains('\\') || link.Split('/').Any(p => p == ""))
                    throw new InvalidDataException("更新包包含不安全的链接。");
                links.Add((target, link));
                continue;
            }
            if (name.EndsWith('/')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew);
            var buffer = new byte[81920];
            long entryBytes = 0;
            int read;
            while ((read = input.Read(buffer)) != 0)
            {
                entryBytes += read;
                actualSize += read;
                if (entryBytes > MaxArchiveBytes || actualSize > 3_000_000_000) throw new InvalidDataException("解压内容过大。");
                output.Write(buffer, 0, read);
            }
            if (mac && unixType == 0x8000 && OperatingSystem.IsMacOS())
                File.SetUnixFileMode(target, (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1FF));
        }
        foreach (var (path, link) in links)
        {
            var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, link));
            if (!File.Exists(resolved) && !Directory.Exists(resolved)) throw new InvalidDataException("更新包包含失效或链式链接。");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.CreateSymbolicLink(path, link);
        }
        if (mac && !File.Exists(Path.Combine(destination, "VodBox.app", "Contents", "MacOS", "VodBox.Desktop")))
            throw new InvalidDataException("更新包缺少可执行文件。");
        if (!mac && !File.Exists(Path.Combine(destination, "VodBox.Desktop.exe")))
            throw new InvalidDataException("更新包缺少可执行文件。");
    }

    public static void ValidateInstallDirectory(string target)
    {
        var parent = Path.GetDirectoryName(target) ?? throw new InvalidOperationException("无安装目录。");
        using var probe = new FileStream(Path.Combine(parent, ".vodbox-write-" + Guid.NewGuid().ToString("N")), FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    internal static string BuildMacScript(string target, string incoming, string stage, string backup, string cache, int pid)
    {
        static string Q(string text) => "'" + text.Replace("'", "'\\''") + "'";
        return $"#!/bin/sh\nset -eu\nwhile kill -0 {pid} 2>/dev/null; do sleep 1; done\n" +
            $"cleanup() {{ rm -rf {Q(stage)} {Q(cache)}; }}\ntrap cleanup EXIT\n" +
            $"mv {Q(target)} {Q(backup)}\nif mv {Q(incoming)} {Q(target)}; then\n" +
            $"  if /usr/bin/codesign --verify --deep --strict {Q(target)} && /usr/bin/open {Q(target)}; then rm -rf {Q(backup)}; exit 0; fi\n" +
            $"  mv {Q(target)} {Q(incoming)}\nfi\nmv {Q(backup)} {Q(target)}\n";
    }

    public static async Task PrepareAndLaunchAsync(string archive, string processPath, string cache, CancellationToken token)
    {
        if (!IsPackaged(processPath)) throw new InvalidOperationException("开发模式下不能原地更新，请安装正式发行包。");
        var mac = OperatingSystem.IsMacOS();
        var target = mac ? FindMacApp(processPath)! : Path.GetDirectoryName(processPath)!;
        ValidateInstallDirectory(target);
        var parent = Path.GetDirectoryName(target)!;
        var stage = Path.Combine(parent, ".vodbox-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            ExtractSafe(archive, stage, mac);
            var incoming = mac ? Path.Combine(stage, "VodBox.app") : stage;
            if (mac)
            {
                using var verify = Process.Start(new ProcessStartInfo("/usr/bin/codesign") { ArgumentList = { "--verify", "--deep", "--strict", incoming }, UseShellExecute = false })!;
                await verify.WaitForExitAsync(token);
                if (verify.ExitCode != 0) throw new InvalidDataException("app bundle 签名校验失败。");
            }
            token.ThrowIfCancellationRequested();
            var script = Path.Combine(cache, mac ? "apply.sh" : "apply.ps1");
            var backup = Path.Combine(parent, ".vodbox-backup-" + Guid.NewGuid().ToString("N"));
            if (mac)
            {
                var body = BuildMacScript(target, incoming, stage, backup, cache, Environment.ProcessId);
                await File.WriteAllTextAsync(script, body, token);
                _ = Process.Start(new ProcessStartInfo("/bin/sh") { ArgumentList = { script }, UseShellExecute = false }) ?? throw new InvalidOperationException("无法启动独立更新器。");
            }
            else
            {
                static string Ps(string text) => "'" + text.Replace("'", "''") + "'";
                var body = $"while (Get-Process -Id {Environment.ProcessId} -ErrorAction SilentlyContinue) {{ Start-Sleep -Seconds 1 }}\n" +
                    $"$target={Ps(target)}; $stage={Ps(incoming)}; $backup={Ps(backup)}\n" +
                    "try { Move-Item -LiteralPath $target -Destination $backup -ErrorAction Stop; " +
                    "try { Move-Item -LiteralPath $stage -Destination $target -ErrorAction Stop; " +
                    "Start-Process -FilePath (Join-Path $target 'VodBox.Desktop.exe') } " +
                    "catch { if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }; Move-Item -LiteralPath $backup -Destination $target -ErrorAction Stop; throw }; " +
                    $"Remove-Item -LiteralPath {Ps(backup)} -Recurse -Force -ErrorAction SilentlyContinue; Remove-Item -LiteralPath {Ps(cache)} -Recurse -Force -ErrorAction SilentlyContinue " +
                    $"}} catch {{ if (Test-Path -LiteralPath {Ps(stage)}) {{ Remove-Item -LiteralPath {Ps(stage)} -Recurse -Force -ErrorAction SilentlyContinue }}; exit 1 }}\n";
                await File.WriteAllTextAsync(script, body, token);
                _ = Process.Start(new ProcessStartInfo("powershell.exe") { ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script }, UseShellExecute = false, CreateNoWindow = true }) ?? throw new InvalidOperationException("无法启动独立更新器。");
            }
        }
        catch { if (Directory.Exists(stage)) Directory.Delete(stage, true); throw; }
    }
}
