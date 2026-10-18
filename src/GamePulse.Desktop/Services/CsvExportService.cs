using System.Text;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GamePulse.Core;
using GamePulse.Desktop.Resources;

namespace GamePulse.Desktop.Services;

public enum ExportSaveResult { Saved, Cancelled, Failed }

/// <summary>The user chooses a destination for a captured table. No automatic persistence, network or path logging.</summary>
public static class CsvExportService
{
    public static async Task<ExportSaveResult> SaveAsync(TopLevel owner, ExportTable table, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            if (!owner.StorageProvider.CanSave) return ExportSaveResult.Failed;
            var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = UiText.Get("ExportDialogTitle"),
                SuggestedFileName = $"GamePulse-{table.Kind}-{DateTimeOffset.UtcNow:yyyy-MM-dd-HHmmss}.csv",
                DefaultExtension = "csv", ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"], MimeTypes = ["text/csv"] }]
            });
            if (file is null) return ExportSaveResult.Cancelled;
            using (file)
                return await WriteFileAsync(file.OpenWriteAsync, table, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return ExportSaveResult.Cancelled; }
        catch (Exception) { return ExportSaveResult.Failed; } // Never show a user's filesystem path or raw exception.
    }

    /// <summary>Writes a destination already explicitly chosen by the user. The callback transfers ownership of its stream.</summary>
    public static async Task<ExportSaveResult> WriteFileAsync(Func<Task<Stream>> openWrite, ExportTable table, CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            await Task.Run(async () =>
            {
                await using var stream = await openWrite().ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (stream.CanSeek)
                {
                    // Truncation alone need not reset a provider's current stream position.
                    stream.SetLength(0);
                    stream.Position = 0;
                }
                await using var writer = new StreamWriter(stream, new UTF8Encoding(true), 8192, leaveOpen: false);
                await DataExport.WriteCsvAsync(writer, table, token).ConfigureAwait(false);
                await writer.FlushAsync(token).ConfigureAwait(false);
            }, token).ConfigureAwait(false);
            return ExportSaveResult.Saved;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return ExportSaveResult.Cancelled; }
        catch (Exception) { return ExportSaveResult.Failed; }
    }
}
