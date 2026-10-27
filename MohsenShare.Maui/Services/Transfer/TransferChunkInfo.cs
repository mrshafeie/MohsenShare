
using System;
using System.IO;

namespace MohsenShare.Maui.Services.Transfer;

/// <summary>
/// Describes a binary chunk within a file transfer.
/// Offsets are absolute byte positions within the original file.
/// </summary>
public sealed record TransferChunkInfo
{
    public string FileId { get; init; } = string.Empty;

    public long Offset { get; init; }

    public int Length { get; init; }

    public long EndOffset => checked(Offset + Length);

    public bool IsFinalChunk(long totalFileLength)
    {
        Validate(totalFileLength);

        return EndOffset == totalFileLength;
    }

    public void Validate(long totalFileLength)
    {
        if (string.IsNullOrWhiteSpace(FileId))
        {
            throw new InvalidDataException(
                "The chunk file ID is missing.");
        }

        if (totalFileLength < 0 ||
            totalFileLength > TransferProtocol.MaxFileSize)
        {
            throw new InvalidDataException(
                "The total file length is invalid.");
        }

        if (Offset < 0 || Offset > totalFileLength)
        {
            throw new InvalidDataException(
                "The chunk offset is outside the file.");
        }

        if (Length <= 0 ||
            Length > TransferProtocol.DefaultChunkSize)
        {
            throw new InvalidDataException(
                "The chunk length is outside the supported range.");
        }

        long endOffset;

        try
        {
            endOffset = checked(Offset + Length);
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException(
                "The chunk end offset overflowed.",
                exception);
        }

        if (endOffset > totalFileLength)
        {
            throw new InvalidDataException(
                "The chunk exceeds the total file length.");
        }
    }
}
