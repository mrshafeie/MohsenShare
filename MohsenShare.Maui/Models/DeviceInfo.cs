namespace MohsenShare.Maui.Models;

public sealed class DeviceInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public string Name { get; set; } = string.Empty;

    public string IpAddress { get; set; } = string.Empty;

    public int Port { get; set; }

    public string Platform { get; set; } = string.Empty;

    public DateTime LastSeen { get; set; } = DateTime.UtcNow;
}