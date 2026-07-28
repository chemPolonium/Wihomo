namespace Wihomo.Models;

public sealed class ConnectionStats
{
    public long DownloadTotal { get; set; }
    public long UploadTotal { get; set; }
    public int ActiveConnections { get; set; }
    public double DownloadBytesPerSecond { get; set; }
    public double UploadBytesPerSecond { get; set; }
}
