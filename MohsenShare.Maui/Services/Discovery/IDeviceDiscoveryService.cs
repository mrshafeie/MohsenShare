using System;
using System.Collections.Generic;
using System.Text;
using MohsenShare.Maui.Models;
namespace MohsenShare.Maui.Services.Discovery
{
    public interface IDeviceDiscoveryService
    {
        event EventHandler<MohsenShare.Maui.Models.DeviceInfo>? DeviceDiscovered;

        event EventHandler<string>? DeviceLost;

        Task StartAsync(CancellationToken cancellationToken = default);

        Task StopAsync();

        IReadOnlyCollection<Models.DeviceInfo> GetDevices();
    }
}
