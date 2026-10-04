namespace VodBox.Core;

public sealed record ResolverDefinition
{
    public required string Id { get; set; }
    public required string Name { get; set; }
    public ResolutionKind Kind { get; set; } = ResolutionKind.Json;
    public string? Entry { get; set; }
    public string UrlPath { get; set; } = "data.url";
    public string? HeadersPath { get; set; } = "data.headers";
    public string? NextResolverId { get; set; }
    public Dictionary<string, string> Headers { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 20;
    public string? BrowserExecutable { get; set; }
    public bool VisibleBrowser { get; set; }
}
