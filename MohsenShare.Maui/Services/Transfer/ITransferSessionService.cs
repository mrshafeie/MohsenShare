
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace MohsenShare.Maui.Services.Transfer;

public interface ITransferSessionService : IAsyncDisposable
{
    event EventHandler<TransferSessionStateChangedEventArgs>?
        SessionStateChanged;

    event EventHandler<TransferProgressChangedEventArgs>?
        ProgressChanged;

    event EventHandler<TransferFileReceivedEventArgs>?
        FileReceived;

    event EventHandler<TransferErrorEventArgs>?
        ErrorOccurred;

    TransferSessionState State { get; }

    TransferConnectionInfo? CurrentConnection { get; }

    /// <summary>
    /// Starts the receiver and returns the connection details
    /// that can be displayed as a QR code.
    /// </summary>
    Task<TransferConnectionInfo> StartListeningAsync(
        string? localDeviceName = null,
        IPAddress? preferredAddress = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Connects to a receiving device.
    /// </summary>
    Task ConnectAsync(
        string ipAddress,
        int port,
        string token,
        string? localDeviceName = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds one or more local files to the transfer queue.
    /// </summary>
    Task QueueFilesAsync(
        IEnumerable<string> filePaths,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts to resume a previously interrupted transfer.
    /// </summary>
    Task ResumeAsync(
        string fileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels a queued or active transfer.
    /// </summary>
    Task CancelAsync(
        string fileId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns persisted transfer records.
    /// </summary>
    Task<IReadOnlyList<TransferResumeRecord>> GetTransfersAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ends the current session and releases network resources.
    /// </summary>
    Task DisconnectAsync(
        CancellationToken cancellationToken = default);
}

public sealed record TransferConnectionInfo
{
    public string SessionId { get; init; } = string.Empty;

    public string DeviceName { get; init; } = string.Empty;

    public string IpAddress { get; init; } = string.Empty;

    public int Port { get; init; }

    public string Token { get; init; } = string.Empty;

    public int ProtocolVersion { get; init; } =
        TransferProtocol.CurrentVersion;

    public string CreateQrPayload()
    {
        return System.Text.Json.JsonSerializer.Serialize(
            new QrConnectionPayload
            {
                SessionId = SessionId,
                DeviceName = DeviceName,
                IpAddress = IpAddress,
                Port = Port,
                Token = Token,
                ProtocolVersion = ProtocolVersion
            },
            TransferProtocol.JsonOptions);
    }
}

public sealed record QrConnectionPayload
{
    public string SessionId { get; init; } = string.Empty;

    public string DeviceName { get; init; } = string.Empty;

    public string IpAddress { get; init; } = string.Empty;

    public int Port { get; init; }

    public string Token { get; init; } = string.Empty;

    public int ProtocolVersion { get; init; } =
        TransferProtocol.CurrentVersion;
}

public sealed class TransferSessionStateChangedEventArgs : EventArgs
{
    public TransferSessionStateChangedEventArgs(
        TransferSessionState previousState,
        TransferSessionState currentState,
        string? message = null)
    {
        PreviousState = previousState;
        CurrentState = currentState;
        Message = message;
    }

    public TransferSessionState PreviousState { get; }

    public TransferSessionState CurrentState { get; }

    public string? Message { get; }
}

public sealed class TransferProgressChangedEventArgs : EventArgs
{
    public TransferProgressChangedEventArgs(
        TransferResumeRecord transfer,
        long bytesPerSecond,
        TimeSpan? estimatedRemaining = null)
    {
        Transfer = transfer;
        BytesPerSecond = Math.Max(0, bytesPerSecond);
        EstimatedRemaining = estimatedRemaining;
    }

    public TransferResumeRecord Transfer { get; }

    public long BytesPerSecond { get; }

    public TimeSpan? EstimatedRemaining { get; }

    public double ProgressPercentage =>
        Transfer.ProgressPercentage;
}

public sealed class TransferFileReceivedEventArgs : EventArgs
{
    public TransferFileReceivedEventArgs(
        string fileId,
        string fileName,
        string location,
        long length)
    {
        FileId = fileId;
        FileName = fileName;
        Location = location;
        Length = length;
    }

    public string FileId { get; }

    public string FileName { get; }

    public string Location { get; }

    public long Length { get; }
}

public sealed class TransferErrorEventArgs : EventArgs
{
    public TransferErrorEventArgs(
        string message,
        string? fileId = null,
        Exception? exception = null)
    {
        Message = message;
        FileId = fileId;
        Exception = exception;
    }

    public string Message { get; }

    public string? FileId { get; }

    public Exception? Exception { get; }
}
