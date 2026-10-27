
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace MohsenShare.Maui.Services.Transfer;

public sealed record TransferFileDescriptor
{
    public string FileId { get; init; } = Guid.NewGuid().ToString("N");

    public string FileName { get; init; } = string.Empty;

    public string SourcePath { get; init; } = string.Empty;

    public long FileLength { get; init; }

    public string? Sha256 { get; init; }

    public DateTime LastModifiedUtc { get; init; }

    public static async Task<TransferFileDescriptor> CreateAsync(
        string filePath,
        bool calculateHash = true,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException(
                "A file path is required.",
                nameof(filePath));
        }

        cancellationToken.ThrowIfCancellationRequested();

        string fullPath = Path.GetFullPath(filePath);

        var fileInfo = new FileInfo(fullPath);

        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException(
                "The selected file does not exist.",
                fullPath);
        }

        if ((fileInfo.Attributes & FileAttributes.Directory) != 0)
        {
            throw new ArgumentException(
                "A directory cannot be added as a file transfer.",
                nameof(filePath));
        }

        if (fileInfo.Length > TransferProtocol.MaxFileSize)
        {
            throw new NotSupportedException(
                $"The selected file exceeds the maximum supported size of " +
                $"{TransferProtocol.MaxFileSize} bytes.");
        }

        string? sha256 = null;

        if (calculateHash)
        {
            sha256 = await TransferFileIntegrity.ComputeFileHashAsync(
                fullPath,
                cancellationToken);
        }

        // Re-read metadata because the file may have changed while
        // its hash was being calculated.
        fileInfo.Refresh();

        if (!fileInfo.Exists)
        {
            throw new FileNotFoundException(
                "The selected file disappeared while it was being prepared.",
                fullPath);
        }

        return new TransferFileDescriptor
        {
            FileId = Guid.NewGuid().ToString("N"),
            FileName = Path.GetFileName(fullPath),
            SourcePath = fullPath,
            FileLength = fileInfo.Length,
            Sha256 = sha256,
            LastModifiedUtc = fileInfo.LastWriteTimeUtc
        };
    }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FileId))
        {
            throw new InvalidDataException(
                "The transfer file ID is missing.");
        }

        if (string.IsNullOrWhiteSpace(FileName) ||
            FileName != Path.GetFileName(FileName) ||
            FileName is "." or "..")
        {
            throw new InvalidDataException(
                "The transfer file name is invalid.");
        }

        if (string.IsNullOrWhiteSpace(SourcePath))
        {
            throw new InvalidDataException(
                "The source file path is missing.");
        }

        if (FileLength < 0 ||
            FileLength > TransferProtocol.MaxFileSize)
        {
            throw new InvalidDataException(
                "The file length is outside the supported range.");
        }

        if (Sha256 is not null)
        {
            _ = TransferFileIntegrity.NormalizeHash(Sha256);
        }
    }
}
