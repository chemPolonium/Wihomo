namespace Wihomo.Models;

public sealed class SubscriptionItem
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public int IntervalSeconds { get; set; } = 3600;
    public bool Enabled { get; set; } = true;
    public long UploadBytes { get; set; }
    public long DownloadBytes { get; set; }
    public long TotalBytes { get; set; }
    public DateTimeOffset? ExpireAt { get; set; }
}
