using System.Collections;
using System.Globalization;
using GamePulse.Core;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class DataExportTests
{
    [Fact]
    public async Task Csv_quotes_every_cell_escapes_quotes_and_keeps_unicode_and_embedded_newlines()
    {
        using var writer = new StringWriter();
        var table = new ExportTable("test", ["name", "notes", "unknown"],
            [new string?[] { "Гра, \"Пульс\" 🎮", "Line 1\r\nLine 2", null }]);
        await DataExport.WriteCsvAsync(writer, table, default);
        Assert.Equal("\"name\",\"notes\",\"unknown\"\r\n\"Гра, \"\"Пульс\"\" 🎮\",\"Line 1\r\nLine 2\",\"\"\r\n", writer.ToString());
    }

    [Theory]
    [InlineData("=1+1")]
    [InlineData("+SUM(1,2)")]
    [InlineData("-2+3")]
    [InlineData("@SUM(1,2)")]
    [InlineData("   =1+1")]
    [InlineData("\tplain")]
    [InlineData("\rplain")]
    [InlineData("\nplain")]
    [InlineData("\uFF1D1+1")]
    [InlineData("\uFF0B1+1")]
    [InlineData("\uFF0D1+1")]
    [InlineData("\uFF20SUM(1,2)")]
    public void Spreadsheet_formula_and_leading_control_cells_are_marked_as_text(string value) =>
        Assert.Equal("\"'" + value + "\"", DataExport.Escape(value));

    [Fact]
    public void Unexpected_control_characters_are_removed_before_formula_detection()
    {
        Assert.Equal("\"'=1+1\"", DataExport.Escape("\0\u001B=1+1\u007F"));
        Assert.Equal("\"ordinary\tname\"", DataExport.Escape("ordinary\tname"));
        Assert.Equal("\"\"", DataExport.Escape(null));
    }

    [Theory]
    [InlineData("76561198000000001")]
    [InlineData("18446744073709551615")]
    public async Task Steam_and_asset_identifiers_keep_all_digits_as_spreadsheet_text(string id)
    {
        using var writer = new StringWriter();
        await DataExport.WriteCsvAsync(writer, new("test", ["id"], [new[] { DataExport.Identifier(id) }]), default);
        Assert.Equal("\"id\"\r\n\"'" + id + "\"\r\n", writer.ToString());
    }

    [Fact]
    public void Scalar_values_are_invariant_and_missing_values_stay_blank()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("uk-UA");
            Assert.Equal("9223372036854775807", DataExport.Number(long.MaxValue));
            Assert.Equal("0", DataExport.Number(0));
            Assert.Null(DataExport.Number(-1)); Assert.Null(DataExport.Number(null));
            Assert.Equal("true", DataExport.Flag(true)); Assert.Equal("false", DataExport.Flag(false)); Assert.Null(DataExport.Flag(null));
            Assert.Equal("2026-10-18T08:30:00.0000000+00:00", DataExport.Timestamp(new(2026, 10, 18, 11, 30, 0, TimeSpan.FromHours(3))));
            Assert.Null(DataExport.Timestamp(null));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_table_shape_fails_before_any_bytes_are_written(bool emptyHeaders)
    {
        using var writer = new StringWriter();
        var table = emptyHeaders ? new ExportTable("bad", [], []) : new("bad", ["one"], [new string?[] { "one", "extra" }]);
        await Assert.ThrowsAsync<ArgumentException>(() => DataExport.WriteCsvAsync(writer, table, default));
        Assert.Equal("", writer.ToString());
    }

    [Fact]
    public async Task Precancelled_export_never_enumerates_rows_or_writes_header()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        using var writer = new StringWriter();
        var rows = new ForbiddenRows();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DataExport.WriteCsvAsync(writer,
            new("test", ["one"], rows), cancellation.Token));
        Assert.False(rows.Enumerated); Assert.Equal("", writer.ToString());
    }

    [Fact]
    public async Task Cancellation_during_writing_stops_before_the_next_row()
    {
        using var cancellation = new CancellationTokenSource();
        using var writer = new CancelAfterHeaderWriter(cancellation);
        var table = new ExportTable("test", ["one"], [new string?[] { "never written" }]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DataExport.WriteCsvAsync(writer, table, cancellation.Token));
        Assert.Equal("\"one\"\r\n", writer.ToString());
    }

    private sealed class ForbiddenRows : IReadOnlyList<IReadOnlyList<string?>>
    {
        public bool Enumerated { get; private set; }
        public int Count => 1;
        public IReadOnlyList<string?> this[int index] => throw new InvalidOperationException("Rows must not be read.");
        public IEnumerator<IReadOnlyList<string?>> GetEnumerator()
        { Enumerated = true; throw new InvalidOperationException("Rows must not be read."); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class CancelAfterHeaderWriter(CancellationTokenSource cancellation) : StringWriter
    {
        public override Task WriteAsync(ReadOnlyMemory<char> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            cancellation.Cancel();
            return Task.CompletedTask;
        }
    }
}
