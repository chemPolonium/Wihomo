namespace Wihomo.Models;

public sealed class ProxyGroupInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string Current { get; set; } = string.Empty;
    public List<string> Options { get; set; } = [];
}
