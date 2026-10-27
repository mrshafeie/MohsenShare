
namespace MohsenShare.Maui.Services.Transfer;

/// <summary>
/// اطلاعات پایدار یک انتقال برای بازیابی پس از قطع اتصال یا بسته‌شدن برنامه.
/// </summary>
public sealed record TransferResumeRecord
{
    public int SchemaVersion { get; init; } = 1;

    public string FileId { get; init; } = Guid.NewGuid().ToString("N");

    public string SessionId { get; init; } = string.Empty;

    public string FileName { get; init; } = string.Empty;

    /// <summary>
    /// مسیر فایل اصلی در دستگاه فرستنده.
    /// ممکن است پس از بسته‌شدن برنامه دیگر در دسترس نباشد.
    /// </summary>
    public string? SourcePath { get; init; }

    /// <summary>
    /// مسیر فایل موقت در دستگاه گیرنده.
    /// </summary>
    public string? PartialFilePath { get; init; }

    /// <summary>
    /// مسیر نهایی فایل پس از تکمیل و تأیید.
    /// </summary>
    public string? FinalFilePath { get; init; }

    public long TotalBytes { get; init; }

    /// <summary>
    /// آخرین موقعیتی که واقعاً روی دیسک ذخیره و ثبت شده است.
    /// </summary>
    public long PersistedBytes { get; init; }

    public string? ExpectedSha256 { get; init; }

    public string? ActualSha256 { get; init; }

    public TransferDirection Direction { get; init; }

    public TransferFileState State { get; init; } =
        TransferFileState.Queued;

    public DateTime CreatedAtUtc { get; init; } =
        DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; init; } =
        DateTime.UtcNow;

    public DateTime? CompletedAtUtc { get; init; }

    public string? LastError { get; init; }

    public bool IsResumable =>
        State is TransferFileState.Paused
            or TransferFileState.Reconnecting
            or TransferFileState.Failed
            or TransferFileState.Transferring;

    public double ProgressPercentage =>
        TotalBytes <= 0
            ? 0
            : Math.Clamp(
                PersistedBytes * 100d / TotalBytes,
                0d,
                100d);

    /// <summary>
    /// اعتبارسنجی رکورد پیش از ذخیره یا بازیابی.
    /// </summary>
    public void Validate()
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidOperationException(
                $"نسخه رکورد انتقال پشتیبانی نمی‌شود: {SchemaVersion}");
        }

        if (string.IsNullOrWhiteSpace(FileId))
        {
            throw new InvalidOperationException(
                "شناسه فایل الزامی است.");
        }

        if (string.IsNullOrWhiteSpace(FileName))
        {
            throw new InvalidOperationException(
                "نام فایل الزامی است.");
        }

        if (TotalBytes < 0)
        {
            throw new InvalidOperationException(
                "اندازه فایل نمی‌تواند منفی باشد.");
        }

        if (PersistedBytes < 0 || PersistedBytes > TotalBytes)
        {
            throw new InvalidOperationException(
                "موقعیت ذخیره‌شده خارج از محدوده فایل است.");
        }

        if (State == TransferFileState.Completed &&
            PersistedBytes != TotalBytes)
        {
            throw new InvalidOperationException(
                "فایل تکمیل‌شده باید تمام بایت‌ها را داشته باشد.");
        }
    }
}
