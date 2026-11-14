using ChannelWave.Host.Linux;
using Xunit;

namespace ChannelWave.Core.Tests;

public sealed class LinuxProfileSecurityTests
{
    [Fact]
    public void Existing_profile_is_private_and_symlink_target_is_never_modified()
    {
        if (!OperatingSystem.IsLinux()) return;
        // Arrange: emulate an old profile created under umask 022.
        var parent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var profile = Path.Combine(parent, "profile");
        var target = Path.Combine(parent, "target");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(target);
        var publicMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
        File.SetUnixFileMode(profile, publicMode);
        File.SetUnixFileMode(target, publicMode);
        try
        {
            // Act.
            LinuxProfileSecurity.Prepare(profile);
            Directory.CreateSymbolicLink(Path.Combine(parent, "link"), target);

            // Assert.
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(profile));
            Assert.Throws<IOException>(() => LinuxProfileSecurity.Prepare(Path.Combine(parent, "link")));
            Assert.Equal(publicMode, File.GetUnixFileMode(target));
        }
        finally { Directory.Delete(parent, true); }
    }
}
