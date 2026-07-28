namespace Wihomo.Models;

public sealed class ConnectionInfo
{
    public string Source { get; init; } = string.Empty;
    public string Destination { get; init; } = string.Empty;
    public string Type { get; init; } = string.Empty;
    public string UsedProxy { get; init; } = string.Empty;
    public string Rule { get; init; } = string.Empty;
    public string Speed { get; init; } = string.Empty;
}
