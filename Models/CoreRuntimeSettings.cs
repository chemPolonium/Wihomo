namespace Wihomo.Models;

public sealed class CoreRuntimeSettings
{
    public string CoreExecutablePath { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public string ExternalControllerHost { get; set; } = "127.0.0.1";
    public int ExternalControllerPort { get; set; } = 9090;
    public string Secret { get; set; } = "wihomo";
    public int MixedPort { get; set; } = 8090;
    public int SocksPort { get; set; } = 8091;
    public int HttpPort { get; set; } = 8092;
    public bool EnableSystemProxy { get; set; }
    public bool EnableTun { get; set; }
    public string TunStack { get; set; } = "mixed";
}
