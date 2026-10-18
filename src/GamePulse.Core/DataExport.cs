using System.Globalization;

namespace GamePulse.Core;

/// <summary>A snapshot captured by an explicit export action, never a live binding or network source.</summary>
public sealed record ExportTable(string Kind, IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string?>> Rows);

public static class DataExport
{
    /// <summary>RFC 4180 escaping, invariant raw values and protection from spreadsheet formula interpretation.</summary>
    public static async Task WriteCsvAsync(TextWriter destination, ExportTable table, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(table);
        cancellationToken.ThrowIfCancellationRequested();
        if (table.Headers.Count == 0)
            throw new ArgumentException("Export rows must match the header shape.", nameof(table));
        foreach (var row in table.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Count != table.Headers.Count)
                throw new ArgumentException("Export rows must match the header shape.", nameof(table));
        }
        await WriteRowAsync(destination, table.Headers, cancellationToken).ConfigureAwait(false);
        foreach (var row in table.Rows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await WriteRowAsync(destination, row, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task WriteRowAsync(TextWriter writer, IEnumerable<string?> values, CancellationToken token) =>
        writer.WriteAsync((string.Join(',', values.Select(Escape)) + "\r\n").AsMemory(), token);

    public static string Escape(string? value)
    {
        value ??= "";
        // Keep ordinary Unicode names/newlines, but never emit NUL or unexpected control sequences.
        value = new string(value.Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t').ToArray());
        var significant = value.AsSpan().TrimStart();
        if (!significant.IsEmpty && (significant[0] is '=' or '+' or '-' or '@' or '\uFF1D' or '\uFF0B' or '\uFF0D' or '\uFF20'
            || value[0] is '\t' or '\r' or '\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    // Excel otherwise rounds 64-bit Steam/asset IDs. The documented text marker preserves their digits.
    public static string Identifier(string value) => "'" + value;
    public static string? Number(long? value) => value is >= 0 ? value.Value.ToString(CultureInfo.InvariantCulture) : null;
    public static string? Flag(bool? value) => value switch { true => "true", false => "false", _ => null };
    public static string? Timestamp(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
