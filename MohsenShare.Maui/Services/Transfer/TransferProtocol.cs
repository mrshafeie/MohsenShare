
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MohsenShare.Maui.Services.Transfer;

/// <summary>
/// قرارداد پیام‌های پروتکل انتقال MohsenShare.
/// تمام پیام‌های کنترلی به‌صورت JSON و با جداکننده newline ارسال می‌شوند.
/// داده‌های فایل پس از پیام کنترلی مربوطه به‌صورت باینری منتقل می‌شوند.
/// </summary>
public static class TransferProtocol
{
    public const int CurrentVersion = 1;

    public const int MaxControlMessageBytes = 16 * 1024;

    public const int DefaultChunkSize = 1024 * 1024;

    public const long MaxFileSize = 10L * 1024 * 1024 * 1024;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        MaxDepth = 16
    };

    public static byte[] SerializeMessage(TransferMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        byte[] json = JsonSerializer.SerializeToUtf8Bytes(
            message,
            JsonOptions);

        if (json.Length > MaxControlMessageBytes)
        {
            throw new InvalidDataException(
                "پیام کنترلی از حداکثر اندازه مجاز بزرگ‌تر است.");
        }

        byte[] framed = new byte[json.Length + 1];

        Buffer.BlockCopy(
            json,
            0,
            framed,
            0,
            json.Length);

        framed[^1] = (byte)'\n';

        return framed;
    }

    public static TransferMessage DeserializeMessage(
        ReadOnlySpan<byte> json)
    {
        if (json.IsEmpty || json.Length > MaxControlMessageBytes)
        {
            throw new InvalidDataException(
                "اندازه پیام کنترلی معتبر نیست.");
        }

        TransferMessage? message =
            JsonSerializer.Deserialize<TransferMessage>(
                json,
                JsonOptions);

        if (message is null)
        {
            throw new InvalidDataException(
                "پیام انتقال خالی یا نامعتبر است.");
        }

        if (message.Version != CurrentVersion)
        {
            throw new InvalidDataException(
                $"نسخه پروتکل پشتیبانی نمی‌شود: {message.Version}");
        }

        if (string.IsNullOrWhiteSpace(message.Type))
        {
            throw new InvalidDataException(
                "نوع پیام مشخص نشده است.");
        }

        return message;
    }
}

/// <summary>
/// نام پیام‌های کنترلی پروتکل.
/// </summary>
public static class TransferMessageTypes
{
    public const string Hello = "hello";
    public const string HelloAccepted = "helloAccepted";

    public const string FileStart = "fileStart";
    public const string FileAccepted = "fileAccepted";
    public const string FileRejected = "fileRejected";

    public const string ChunkStart = "chunkStart";
    public const string ChunkAccepted = "chunkAccepted";

    public const string FileComplete = "fileComplete";
    public const string FileVerified = "fileVerified";

    public const string ResumeQuery = "resumeQuery";
    public const string ResumePosition = "resumePosition";

    public const string CancelFile = "cancelFile";

    public const string Ping = "ping";
    public const string Pong = "pong";

    public const string EndSession = "endSession";
    public const string SessionEnded = "sessionEnded";

    public const string Error = "error";
}

/// <summary>
/// پیام مشترک کنترلی بین دو دستگاه.
/// فیلدهای غیرمرتبط با نوع پیام، null باقی می‌مانند.
/// </summary>
public sealed record TransferMessage
{
    public int Version { get; init; } =
        TransferProtocol.CurrentVersion;

    public string Type { get; init; } = string.Empty;

    public string? SessionId { get; init; }

    public string? Token { get; init; }

    public string? DeviceId { get; init; }

    public string? DeviceName { get; init; }

    public string? FileId { get; init; }

    public string? FileName { get; init; }

    public long? FileLength { get; init; }

    /// <summary>
    /// موقعیت بایتی فایل برای Resume.
    /// این مقدار باید توسط گیرنده و بر اساس داده ذخیره‌شده تأیید شود.
    /// </summary>
    public long? Offset { get; init; }

    /// <summary>
    /// تعداد بایت‌های payload که بلافاصله پس از پیام کنترلی می‌آیند.
    /// </summary>
    public int? ChunkLength { get; init; }

    /// <summary>
    /// هش SHA-256 فایل کامل، در قالب hexadecimal.
    /// </summary>
    public string? Sha256 { get; init; }

    public string? ErrorCode { get; init; }

    public string? Message { get; init; }
}

/// <summary>
/// وضعیت نشست انتقال.
/// </summary>
public enum TransferSessionState
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    Reconnecting = 3,
    Closing = 4,
    Failed = 5
}

/// <summary>
/// وضعیت هر فایل در صف انتقال.
/// </summary>
public enum TransferFileState
{
    Queued = 0,
    Preparing = 1,
    Transferring = 2,
    Paused = 3,
    Reconnecting = 4,
    Verifying = 5,
    Completed = 6,
    Cancelled = 7,
    Failed = 8
}

/// <summary>
/// جهت انتقال فایل.
/// </summary>
public enum TransferDirection
{
    Sending = 0,
    Receiving = 1
}
