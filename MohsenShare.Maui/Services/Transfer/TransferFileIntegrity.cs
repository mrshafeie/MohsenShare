
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace MohsenShare.Maui.Services.Transfer;

public static class TransferFileIntegrity
{
    private const int BufferSize = 128 * 1024;

    /// <summary>
    /// Computes the SHA-256 hash of a file on disk.
    /// Returns a lowercase hexadecimal string.
    /// </summary>
    public static async Task<string> ComputeFileHashAsync(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException(
                "A file path is required.",
                nameof(filePath));
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await ComputeStreamHashAsync(
            stream,
            cancellationToken);
    }

    /// <summary>
    /// Computes SHA-256 from the stream's current position
    /// through the end of the stream.
    /// </summary>
    public static async Task<string> ComputeStreamHashAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        if (!stream.CanRead)
        {
            throw new ArgumentException(
                "The stream must support reading.",
                nameof(stream));
        }

        using var hash = IncrementalHash.CreateHash(
            HashAlgorithmName.SHA256);

        byte[] buffer = new byte[BufferSize];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int read = await stream.ReadAsync(
                buffer.AsMemory(0, buffer.Length),
                cancellationToken);

            if (read == 0)
            {
                break;
            }

            hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    /// <summary>
    /// Compares two SHA-256 hexadecimal strings.
    /// Comparison ignores letter casing.
    /// </summary>
    public static bool HashesMatch(
        string? expectedHash,
        string? actualHash)
    {
        if (string.IsNullOrWhiteSpace(expectedHash) ||
            string.IsNullOrWhiteSpace(actualHash))
        {
            return false;
        }

        return string.Equals(
            NormalizeHash(expectedHash),
            NormalizeHash(actualHash),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Validates and normalizes a SHA-256 hexadecimal string.
    /// </summary>
    public static string NormalizeHash(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
        {
            throw new ArgumentException(
                "A SHA-256 hash is required.",
                nameof(hash));
        }

        string normalized = hash.Trim().ToLowerInvariant();

        if (normalized.Length != 64)
        {
            throw new FormatException(
                "A SHA-256 hash must contain exactly 64 hexadecimal characters.");
        }

        foreach (char character in normalized)
        {
            bool isHex =
                character is >= '0' and <= '9' ||
                character is >= 'a' and <= 'f';

            if (!isHex)
            {
                throw new FormatException(
                    "The SHA-256 hash contains an invalid character.");
            }
        }

        return normalized;
    }
}
