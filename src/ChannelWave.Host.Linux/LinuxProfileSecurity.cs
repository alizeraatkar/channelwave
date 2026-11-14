using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ChannelWave.Host.Linux;

/// <summary>Protect the profile before opening any plaintext library or media files.</summary>
internal static class LinuxProfileSecurity
{
    public static void Prepare(string root)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Linux profile protection requires Linux.");
        Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // Work on the opened directory, never chmod a link or a substituted pathname.
        var descriptor = Open(root, 0x10000 | 0x20000 | 0x80000); // O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC
        if (descriptor < 0) throw Failure();
        try
        {
            if (Statx(descriptor, "", 0x1000, 0x8, out var status) != 0 || (status.Mask & 0x8) == 0)
                throw Failure(); // AT_EMPTY_PATH, STATX_UID
            if (status.UserId != GetEffectiveUserId()) throw new IOException("The ChannelWave profile must belong to the current OS user.");
            if (ChangeMode(descriptor, 0x1c0) != 0) throw Failure(); // 0700
            if (Statx(descriptor, "", 0x1000, 0xa, out status) != 0) throw Failure(); // STATX_UID | STATX_MODE
            if ((status.Mask & 0xa) != 0xa || status.UserId != GetEffectiveUserId() || (status.Mode & 0xfff) != 0x1c0)
                throw new IOException("The ChannelWave profile's owner-only permissions could not be verified.");
        }
        finally { Close(descriptor); }
    }

    private static IOException Failure() => new("Cannot protect the ChannelWave profile. Check its ownership and ensure it is a real directory.", new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct FileStatus
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(20)] public uint UserId;
        [FieldOffset(28)] public ushort Mode;
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(int descriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mask, out FileStatus status);
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();
    [DllImport("libc", EntryPoint = "fchmod", SetLastError = true)]
    private static extern int ChangeMode(int descriptor, uint mode);
    [DllImport("libc", EntryPoint = "close")]
    private static extern int Close(int descriptor);
}
