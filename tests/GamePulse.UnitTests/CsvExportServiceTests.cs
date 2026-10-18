using System.Text;
using GamePulse.Core;
using GamePulse.Desktop.Services;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class CsvExportServiceTests
{
    private static ExportTable Table() => new("games", ["name"], [new string?[] { "Пульс 🎮" }]);

    [Fact]
    public async Task Chosen_file_replaces_longer_contents_resets_position_and_writes_utf8_bom()
    {
        using var stream = new MemoryStream(); stream.Write(Encoding.UTF8.GetBytes(new string('X', 2048))); stream.Position = 17;
        using var file = new File(() => Task.FromResult<Stream>(stream));
        var result = await CsvExportService.WriteFileAsync(file.OpenWriteAsync, Table(), default);
        Assert.Equal(ExportSaveResult.Saved, result); Assert.Equal(1, file.OpenCount);
        var expected = new UTF8Encoding(true).GetPreamble().Concat(Encoding.UTF8.GetBytes("\"name\"\r\n\"Пульс 🎮\"\r\n")).ToArray();
        Assert.Equal(expected, stream.ToArray());
        Assert.False(stream.CanWrite); Assert.False(file.Disposed); // The stream is owned here; the selected file is owned by SaveAsync.
    }

    [Fact]
    public async Task Precancelled_export_does_not_open_or_truncate_the_destination()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var file = new File(() => throw new InvalidOperationException("Must not open a cancelled export."));
        Assert.Equal(ExportSaveResult.Cancelled, await CsvExportService.WriteFileAsync(file.OpenWriteAsync, Table(), cancellation.Token));
        Assert.Equal(0, file.OpenCount);
    }

    [Fact]
    public async Task Cancellation_while_opening_leaves_existing_stream_contents_untouched()
    {
        using var cancellation = new CancellationTokenSource();
        var original = Encoding.UTF8.GetBytes("Previously saved analysis");
        using var stream = new MemoryStream(); stream.Write(original);
        using var file = new File(() => { cancellation.Cancel(); return Task.FromResult<Stream>(stream); });
        Assert.Equal(ExportSaveResult.Cancelled, await CsvExportService.WriteFileAsync(file.OpenWriteAsync, Table(), cancellation.Token));
        Assert.Equal(original, stream.ToArray()); Assert.False(stream.CanWrite);
    }

    [Fact]
    public async Task Storage_errors_are_safe_results_with_no_exception_or_filesystem_path_exposed()
    {
        using var file = new File(() => Task.FromException<Stream>(new IOException("Private filesystem detail must not escape")));
        var result = await CsvExportService.WriteFileAsync(file.OpenWriteAsync, Table(), default);
        Assert.Equal(ExportSaveResult.Failed, result); Assert.Equal(1, file.OpenCount);
    }

    [Fact]
    public async Task Write_failure_returns_failed_and_disposes_the_stream()
    {
        using var stream = new FailedWriteStream();
        using var file = new File(() => Task.FromResult<Stream>(stream));
        Assert.Equal(ExportSaveResult.Failed, await CsvExportService.WriteFileAsync(file.OpenWriteAsync, Table(), default));
        Assert.False(stream.CanWrite);
    }

    private sealed class File(Func<Task<Stream>> open) : IDisposable
    {
        public int OpenCount { get; private set; }
        public bool Disposed { get; private set; }
        public Task<Stream> OpenWriteAsync() { OpenCount++; return open(); }
        public void Dispose() => Disposed = true;
    }
    private sealed class FailedWriteStream : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count) => throw new IOException("Synthetic write failure.");
        public override void Write(ReadOnlySpan<byte> buffer) => throw new IOException("Synthetic write failure.");
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new IOException("Synthetic write failure."));
    }
}
