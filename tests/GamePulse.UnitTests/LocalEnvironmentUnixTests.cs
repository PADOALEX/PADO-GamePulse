using System.Runtime.InteropServices;
using GamePulse.Infrastructure;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace GamePulse.UnitTests;

public sealed class LocalEnvironmentUnixTests
{
    [UnixFact]
    public async Task A_fifo_named_env_without_a_writer_is_rejected_without_waiting()
    {
        var root = Path.Combine(Path.GetTempPath(), "GamePulse-fifo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".env");
        Task<LocalEnvironmentSettings>? reading = null;
        try
        {
            Assert.Equal(0, MakeFifo(path, 0x180)); // POSIX permissions 0600; no contents or real credentials.
            reading = Task.Run(() => LocalEnvironmentFile.ReadAllowedSettings(root));
            var settings = await reading.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Null(settings.ApiKey);
            Assert.Null(settings.UiLanguage);
        }
        finally
        {
            if (reading is { IsCompleted: false })
            {
                // A regression must fail within two seconds without leaving a blocked test worker.
                // Nonblocking writer open releases a mistaken blocking reader, without itself waiting.
                var nonblocking = OperatingSystem.IsMacOS() ? 0x4 : 0x800;
                var writer = OpenPipe(path, 1 | nonblocking);
                if (writer >= 0)
                {
                    using var writerHandle = new SafeFileHandle((IntPtr)writer, ownsHandle: true);
                    await reading.WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            File.Delete(path);
            Directory.Delete(root);
        }
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    private static extern int MakeFifo([MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int OpenPipe([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    private sealed class UnixFactAttribute : FactAttribute
    {
        public UnixFactAttribute()
        {
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
                Skip = "FIFO behavior is exercised on Linux/macOS; Windows has no POSIX FIFO files.";
        }
    }
}
