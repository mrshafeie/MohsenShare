using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

#if ANDROID
using Android.Content;
using Android.OS;
using Android.Provider;
#endif

namespace MohsenShare.Maui.Services.Storage;

/// <summary>
/// ذخیره‌سازی فایل دریافتی در پوشه عمومی MohsenShare.
/// Windows: Downloads\MohsenShare
/// Android 10+: MediaStore / Download/MohsenShare
/// </summary>
public sealed class ReceivedFileStorageService
{
    public const string RootFolderName = "MohsenShare";

    public enum FileCategory
    {
        Images,
        Videos,
        Audio,
        Documents,
        Applications,
        Other
    }

    public sealed record StoredFileInfo(
        string FileName,
        string Location,
        long Length,
        FileCategory Category,
        DateTime SavedAt);

    /// <summary>
    /// داده‌ها را به‌صورت جریانی ذخیره می‌کند؛ فایل کامل در حافظه بارگذاری نمی‌شود.
    /// این متد برای فایل نهایی است. ذخیره موقت و Resume در مرحله اتصال به موتور انتقال اضافه می‌شود.
    /// </summary>
    public async Task<StoredFileInfo> SaveAsync(
        string requestedFileName,
        long expectedLength,
        Stream source,
        IProgress<long>? bytesWritten = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        if (expectedLength < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedLength));

        string fileName = SanitizeFileName(requestedFileName);
        FileCategory category = GetCategory(fileName);
        DateTime savedAt = DateTime.Now;

#if ANDROID
        return await SaveAndroidAsync(
            fileName,
            expectedLength,
            category,
            source,
            bytesWritten,
            savedAt,
            cancellationToken).ConfigureAwait(false);
#else
        return await SaveFileSystemAsync(
            fileName,
            expectedLength,
            category,
            source,
            bytesWritten,
            savedAt,
            cancellationToken).ConfigureAwait(false);
#endif
    }

    public static FileCategory GetCategory(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        if (extension is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" or ".heic" or ".heif" or ".svg")
            return FileCategory.Images;

        if (extension is ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".3gp" or ".m4v" or ".mpeg" or ".mpg")
            return FileCategory.Videos;

        if (extension is ".mp3" or ".wav" or ".flac" or ".aac" or ".m4a" or ".ogg" or ".opus" or ".wma")
            return FileCategory.Audio;

        if (extension is ".pdf" or ".doc" or ".docx" or ".xls" or ".xlsx" or ".ppt" or ".pptx" or ".txt" or ".rtf" or ".csv" or ".md" or ".odt" or ".ods" or ".odp")
            return FileCategory.Documents;

        if (extension is ".apk" or ".msi" or ".exe" or ".appx" or ".msix" or ".deb" or ".rpm" or ".dmg" or ".pkg")
            return FileCategory.Applications;

        return FileCategory.Other;
    }

    public static string GetCategoryFolder(FileCategory category) => category switch
    {
        FileCategory.Images => "Images",
        FileCategory.Videos => "Videos",
        FileCategory.Audio => "Audio",
        FileCategory.Documents => "Documents",
        FileCategory.Applications => "Applications",
        _ => "Other"
    };

    private static string SanitizeFileName(string? fileName)
    {
        string safe = Path.GetFileName((fileName ?? string.Empty).Trim());

        foreach (char invalid in Path.GetInvalidFileNameChars())
            safe = safe.Replace(invalid, '_');

        if (string.IsNullOrWhiteSpace(safe) || safe is "." or "..")
            safe = $"file_{DateTime.UtcNow:yyyyMMdd_HHmmss}";

        // جلوگیری از نام‌های بسیار بلند و ناسازگار
        if (safe.Length > 180)
        {
            string ext = Path.GetExtension(safe);
            string stem = Path.GetFileNameWithoutExtension(safe);
            int allowed = Math.Max(1, 180 - ext.Length);
            safe = stem[..Math.Min(stem.Length, allowed)] + ext;
        }

        return safe;
    }

#if ANDROID
    private static async Task<StoredFileInfo> SaveAndroidAsync(
        string fileName,
        long expectedLength,
        FileCategory category,
        Stream source,
        IProgress<long>? bytesWritten,
        DateTime savedAt,
        CancellationToken cancellationToken)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Q)
            throw new PlatformNotSupportedException(
                "این پیاده‌سازی ذخیره‌سازی عمومی برای Android 10 به بالا طراحی شده است.");

        var resolver = global::Android.App.Application.Context.ContentResolver
     ?? throw new InvalidOperationException("ContentResolver در دسترس نیست.");

        string downloadsDirectory = Android.OS.Environment.DirectoryDownloads;

        string relativePath =
            $"{downloadsDirectory}/{RootFolderName}/{GetCategoryFolder(category)}/";

        string mimeType = GetMimeType(fileName);
        var values = new ContentValues();
        values.Put(MediaStore.IMediaColumns.DisplayName, fileName);
        values.Put(MediaStore.IMediaColumns.MimeType, mimeType);
        values.Put(MediaStore.IMediaColumns.RelativePath, relativePath);
        values.Put(MediaStore.IMediaColumns.IsPending, 1);

        Android.Net.Uri? uri = resolver.Insert(
            MediaStore.Downloads.ExternalContentUri,
            values);

        if (uri is null)
            throw new IOException("ساخت فایل در پوشه عمومی Downloads ناموفق بود.");

        bool published = false;
        try
        {
            await using (Stream? destination = resolver.OpenOutputStream(uri, "w"))
            {
                if (destination is null)
                    throw new IOException("بازکردن فایل مقصد در حافظه عمومی ناموفق بود.");

                await CopyExactlyOrUntilEndAsync(
                    source,
                    destination,
                    expectedLength,
                    bytesWritten,
                    cancellationToken).ConfigureAwait(false);

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var publishValues = new ContentValues();
            publishValues.Put(MediaStore.IMediaColumns.IsPending, 0);
            int updated = resolver.Update(uri, publishValues, null, null);
            if (updated <= 0)
                throw new IOException("ثبت نهایی فایل در پوشه عمومی ناموفق بود.");

            published = true;
            return new StoredFileInfo(fileName, uri.ToString() ?? relativePath,
                expectedLength, category, savedAt);
        }
        finally
        {
            if (!published)
            {
                try { resolver.Delete(uri, null, null); }
                catch { /* خطای پاک‌سازی نباید خطای اصلی را بپوشاند. */ }
            }
        }
    }
#else
    private static async Task<StoredFileInfo> SaveFileSystemAsync(
        string fileName,
        long expectedLength,
        FileCategory category,
        Stream source,
        IProgress<long>? bytesWritten,
        DateTime savedAt,
        CancellationToken cancellationToken)
    {
        string downloads = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads");

        // در برخی سیستم‌ها پوشه UserProfile در دسترس نیست؛ مسیر استاندارد Downloads را جایگزین می‌کنیم.
        if (string.IsNullOrWhiteSpace(downloads) || !Directory.Exists(downloads))
        {
            downloads = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "Downloads");
        }

        string folder = Path.Combine(
            downloads,
            RootFolderName,
            GetCategoryFolder(category));

        Directory.CreateDirectory(folder);

        string destinationPath = GetUniquePath(folder, fileName);
        string partialPath = destinationPath + ".partial";

        try
        {
            await using (var destination = new FileStream(
                partialPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                1024 * 128,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await CopyExactlyOrUntilEndAsync(
                    source,
                    destination,
                    expectedLength,
                    bytesWritten,
                    cancellationToken).ConfigureAwait(false);

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(partialPath, destinationPath);
            return new StoredFileInfo(fileName, destinationPath, expectedLength, category, savedAt);
        }
        catch
        {
            // فایل ناقص عمداً در نسخهٔ فعلی حذف می‌شود؛ نگه‌داری و Resume آن در مرحلهٔ موتور انتقال پیاده می‌شود.
            try { if (File.Exists(partialPath)) File.Delete(partialPath); }
            catch { }
            throw;
        }
    }
#endif

    private static async Task CopyExactlyOrUntilEndAsync(
        Stream source,
        Stream destination,
        long expectedLength,
        IProgress<long>? bytesWritten,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[128 * 1024];
        long total = 0;

        while (total < expectedLength)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int requested = (int)Math.Min(buffer.Length, expectedLength - total);
            int read = await source.ReadAsync(
                buffer.AsMemory(0, requested),
                cancellationToken).ConfigureAwait(false);

            if (read == 0)
                throw new EndOfStreamException(
                    $"انتقال پیش از تکمیل فایل قطع شد. دریافت‌شده: {total:N0} از {expectedLength:N0} بایت.");

            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken).ConfigureAwait(false);

            total += read;
            bytesWritten?.Report(total);
        }

        // عمداً از source بیشتر نمی‌خوانیم؛ ممکن است در آینده stream شامل پیام بعدی پروتکل باشد.
    }

    private static string GetUniquePath(string folder, string fileName)
    {
        string candidate = Path.Combine(folder, fileName);
        if (!File.Exists(candidate))
            return candidate;

        string stem = Path.GetFileNameWithoutExtension(fileName);
        string extension = Path.GetExtension(fileName);

        for (int i = 1; i < 10000; i++)
        {
            candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{stem}_{Guid.NewGuid():N}{extension}");
    }

    private static string GetMimeType(string fileName)
    {
        string extension = Path.GetExtension(fileName).ToLowerInvariant();

        return extension switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".mp4" => "video/mp4",
            ".mkv" => "video/x-matroska",
            ".mp3" => "audio/mpeg",
            ".wav" => "audio/wav",
            ".m4a" => "audio/mp4",
            ".pdf" => "application/pdf",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".json" => "application/json",
            ".zip" => "application/zip",
            ".apk" => "application/vnd.android.package-archive",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".ppt" => "application/vnd.ms-powerpoint",
            ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            _ => "application/octet-stream"
        };
    }
}
