using System.Text;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace GamePulse.Infrastructure;

/// <summary>Only the explicitly supported, local application settings. Values are never interpolated by ToString.</summary>
public sealed class LocalEnvironmentSettings
{
    public string? ApiKey { get; init; }
    public string? UiLanguage { get; init; }

    public override string ToString() => nameof(LocalEnvironmentSettings);
}

/// <summary>
/// Reads only a bounded, seekable local .env file, rejecting known non-file/reparse entries;
/// it never searches parent directories,
/// changes the process environment, expands variables, executes commands, or logs file contents.
/// Unknown keys are ignored. A malformed assignment to a supported key rejects the whole file.
/// Filesystem/encoding failures produce empty settings so normal setup remains available.
/// </summary>
public static class LocalEnvironmentFile
{
    public const int MaximumFileBytes = 16 * 1024;
    private const string ApiKeyName = "GAMEPULSE_STEAM_API_KEY";
    private const string UiLanguageName = "GAMEPULSE_UI_LANGUAGE";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static LocalEnvironmentSettings ReadAllowedSettings(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory)) return new();

        try
        {
            var folder = new DirectoryInfo(directory);
            if (!folder.Exists || (folder.Attributes & FileAttributes.ReparsePoint) != 0) return new();

            var path = Path.Combine(folder.FullName, ".env");
            if (!IsRegularFile(path)) return new();

            // A strict, small bound also applies when the file grows between inspection and opening.
            // On Windows, sharing only for reading prevents replacement or writes during this read.
            using var stream = OpenReadWithoutPipeWait(path);
            if (stream is null || !stream.CanSeek || stream.Length > MaximumFileBytes || !IsRegularFile(path)) return new();

            var bytes = new byte[MaximumFileBytes + 1];
            var length = 0;
            while (length < bytes.Length)
            {
                var read = stream.Read(bytes, length, bytes.Length - length);
                if (read == 0) break;
                length += read;
            }

            if (length > MaximumFileBytes || !IsRegularFile(path)) return new();
            var text = StrictUtf8.GetString(bytes, 0, length);
            if (text.StartsWith('\uFEFF')) text = text[1..];
            return Parse(text);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException or NotSupportedException or System.Security.SecurityException
            or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Do not retain or expose the exception: runtime messages can include local file data.
            return new();
        }
    }

    private static FileStream? OpenReadWithoutPipeWait(string path)
    {
        if (OperatingSystem.IsWindows())
            return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 4096, FileOptions.SequentialScan);

        // FileAttributes does not identify Unix FIFOs. O_NONBLOCK is required before
        // FileStream.CanSeek can reject one; an ordinary read-only open waits for a writer.
        // Values are native OS flags, not the different System.Native PAL constants:
        // Linux: include/uapi/asm-generic/fcntl.h (v6.12); Darwin: bsd/sys/fcntl.h.
        // https://github.com/torvalds/linux/blob/v6.12/include/uapi/asm-generic/fcntl.h
        // https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/fcntl.h
        int flags;
        if (OperatingSystem.IsLinux() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.X86 or Architecture.Arm64)
            flags = 0x800 | 0x20000 | 0x80000; // O_RDONLY(0) | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC.
        else if (OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64)
            flags = 0x4 | 0x100 | 0x01000000;
        else return null; // Unknown native flag ABIs fail closed; environment variables still work.

        var descriptor = UnixOpen(path, flags);
        if (descriptor < 0) return null;
        var handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        try { return new FileStream(handle, FileAccess.Read, bufferSize: 4096, isAsync: false); }
        catch { handle.Dispose(); throw; }
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int UnixOpen([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private static bool IsRegularFile(string path)
    {
        var attributes = File.GetAttributes(path);
        return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) == 0;
    }

    private static LocalEnvironmentSettings Parse(string text)
    {
        string? apiKey = null;
        string? uiLanguage = null;
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            var statement = line.AsSpan().Trim();
            if (statement.IsEmpty || statement[0] == '#') continue;
            if (statement.StartsWith("export", StringComparison.Ordinal)
                && statement.Length > 6 && char.IsWhiteSpace(statement[6]))
                statement = statement[7..].TrimStart();

            var nameEnd = 0;
            while (nameEnd < statement.Length && !char.IsWhiteSpace(statement[nameEnd]) && statement[nameEnd] != '=')
                nameEnd++;
            var name = statement[..nameEnd];
            var isApiKey = name.SequenceEqual(ApiKeyName);
            var isLanguage = name.SequenceEqual(UiLanguageName);
            if (!isApiKey && !isLanguage) continue;

            var remainder = statement[nameEnd..].TrimStart();
            if (remainder.IsEmpty || remainder[0] != '='
                || !TryParseValue(remainder[1..].Trim(), out var value)) return new();

            // Empty entries do not remove an earlier valid value; the last nonempty assignment wins.
            if (value.Length == 0) continue;
            if (isApiKey) apiKey = value;
            else uiLanguage = value;
        }

        return new() { ApiKey = apiKey, UiLanguage = uiLanguage };
    }

    private static bool TryParseValue(ReadOnlySpan<char> input, out string value)
    {
        value = string.Empty;
        if (input.IsEmpty || input[0] == '#') return true;

        ReadOnlySpan<char> content;
        if (input[0] is '\'' or '"')
        {
            var quote = input[0];
            var closing = input[1..].IndexOf(quote);
            if (closing < 0) return false;
            closing++;
            content = input[1..closing];
            var trailing = input[(closing + 1)..];
            if (!trailing.IsEmpty)
            {
                // A comment after a closing quote must be separated by whitespace.
                if (!char.IsWhiteSpace(trailing[0])) return false;
                trailing = trailing.TrimStart();
                if (!trailing.IsEmpty && trailing[0] != '#') return false;
            }
        }
        else
        {
            var end = input.Length;
            for (var index = 0; index < input.Length; index++)
            {
                if (input[index] is '\'' or '"') return false;
                if (input[index] == '#' && (index == 0 || char.IsWhiteSpace(input[index - 1])))
                {
                    end = index;
                    break;
                }
            }
            content = input[..end];
        }

        content = content.Trim();
        foreach (var character in content)
            if (char.IsControl(character)) return false;

        value = content.ToString();
        return true;
    }
}
