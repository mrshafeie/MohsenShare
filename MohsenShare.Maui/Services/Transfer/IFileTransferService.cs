using MohsenShare.Maui.Models;
using System;
using System.Collections.Generic;
using System.Text;

namespace MohsenShare.Maui.Services.Transfer
{
    public interface IFileTransferService
    {
        Task SendFileAsync(
            Models.DeviceInfo device,
            TransferFile file,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default);

        Task<TransferFile> ReceiveFileAsync(
            Stream input,
            string destinationDirectory,
            IProgress<TransferProgress>? progress = null,
            CancellationToken cancellationToken = default);
    }
}
