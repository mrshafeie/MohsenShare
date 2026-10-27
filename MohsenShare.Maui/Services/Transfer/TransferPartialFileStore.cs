
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace MohsenShare.Maui.Services.Transfer;

/// <summary>
/// Stores incoming file data in the application's private storage.
/// Incomplete files remain available for a later resume.
/// Only verified, completed files should be published to public storage.
/// </summary>
public sealed class TransferPartialFileStore
{
    private readonly string _rootDirectory;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    public TransferPartialFileStore()
        : this(Path.Combine(
            FileSystem.Current.AppDataDirectory,
            "TransferState",
            "PartialFiles"))
    {
    }

    public TransferPartialFileStore(string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory))
        {
            throw new ArgumentException(
                "A storage directory is required.",
                nameof(rootDirectory));
        }

        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public async Task InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        await _initializationLock.WaitAsync(cancellationToken);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(_rootDirectory);
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    /// <summary>
    /// Returns the private partial-file path for a transfer.
    /// </summary>
    public async Task<string> GetPartialFilePathAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        ValidateFileId(fileId);

        await InitializeAsync(cancellationToken);

        return Path.Combine(_rootDirectory, fileId + ".partial");
    }

    /// <summary>
    /// Opens or creates a partial file and positions it at the
    /// receiver-confirmed persisted offset.
    /// </summary>
    public async Task<FileStream> OpenForResumeAsync(
        string fileId,
        long persistedOffset,
        CancellationToken cancellationToken = default)
    {
        if (persistedOffset < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(persistedOffset));
        }

        string path = await GetPartialFilePathAsync(
            fileId,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous |
                     FileOptions.SequentialScan);

        try
        {
            if (stream.Length < persistedOffset)
            {
                throw new InvalidDataException(
                    "The partial file is shorter than the persisted offset.");
            }

            // Discard bytes beyond the last confirmed checkpoint.
            if (stream.Length > persistedOffset)
            {
                stream.SetLength(persistedOffset);
                await stream.FlushAsync(cancellationToken);
            }

            stream.Position = persistedOffset;

            return stream;
        }
        catch
        {
            await stream.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Writes a chunk at an explicit offset.
    /// The caller must persist the new offset in TransferResumeStore
    /// only after this method completes successfully.
    /// </summary>
    public async Task WriteChunkAsync(
        string fileId,
        long offset,
        ReadOnlyMemory<byte> data,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(offset));
        }

        string path = await GetPartialFilePathAsync(
            fileId,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        await using var stream = new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous);

        if (stream.Length < offset)
        {
            throw new InvalidDataException(
                "Chunk offset is beyond the current file length.");
        }

        // A duplicate chunk can safely overwrite already received bytes.
        stream.Position = offset;

        await stream.WriteAsync(data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// Gets the current size of a partial file.
    /// This is not automatically a durable resume checkpoint.
    /// </summary>
    public async Task<long> GetPartialFileLengthAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        string path = await GetPartialFilePathAsync(
            fileId,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var info = new FileInfo(path);

        return info.Exists ? info.Length : 0L;
    }

    /// <summary>
    /// Opens a completed partial file for reading and verification.
    /// </summary>
    public async Task<FileStream> OpenForVerificationAsync(
        string fileId,
        long expectedLength,
        CancellationToken cancellationToken = default)
    {
        if (expectedLength < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedLength));
        }

        string path = await GetPartialFilePathAsync(
            fileId,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            options: FileOptions.Asynchronous |
                     FileOptions.SequentialScan);

        if (stream.Length != expectedLength)
        {
            await stream.DisposeAsync();

            throw new InvalidDataException(
                "The received file length does not match the expected length.");
        }

        return stream;
    }

    /// <summary>
    /// Removes the partial file after successful publication
    /// or after the user explicitly deletes the transfer.
    /// </summary>
    public async Task DeletePartialFileAsync(
        string fileId,
        CancellationToken cancellationToken = default)
    {
        string path = await GetPartialFilePathAsync(
            fileId,
            cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void ValidateFileId(string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId))
        {
            throw new ArgumentException(
                "A file ID is required.",
                nameof(fileId));
        }

        // File IDs are identifiers, not arbitrary filesystem paths.
        if (fileId.Length > 128 ||
            fileId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            fileId.Contains('/') ||
            fileId.Contains('\\') ||
            fileId is "." or "..")
        {
            throw new ArgumentException(
                "The file ID is invalid.",
                nameof(fileId));
        }
    }
}
