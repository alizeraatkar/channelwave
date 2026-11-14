namespace ChannelWave.Core;

public enum OfflineHealth { Saved, Missing, Damaged }
public sealed record OfflineCopy(Track Track, string MediaKey, long Bytes, string Hash, DateTimeOffset SavedAt, OfflineHealth Health = OfflineHealth.Saved);
public sealed record StorageUsage(long OfflineBytes, long TemporaryBytes, long OtherBytes, long? SiteUsage = null,
    long? SiteQuota = null, bool Estimated = false, long ProtectedBytes = 0)
{
    public long Total => OfflineBytes + TemporaryBytes + OtherBytes;
}
public sealed record StorageSettings(long TemporaryLimit)
{
    public const string Key = "storage-settings:v1";
    public static readonly long[] Limits = [256_000_000, 512_000_000, 1_000_000_000, 2_000_000_000];
    public static StorageSettings Default(bool browser) => new(browser ? Limits[0] : Limits[2]);
}
