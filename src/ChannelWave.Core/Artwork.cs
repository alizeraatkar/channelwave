using System.Text.Json.Serialization;

namespace ChannelWave.Core;

public enum ArtworkKind { Embedded, AudioThumbnail, ChannelPhoto }

public sealed record ArtworkData(string MediaKey, ArtworkKind Kind, byte[] Data, long CachedAt = 0)
{
    public const int MaxBytes = 512 * 1024;
    public const int MaxCachedImages = 64;
    public const long MaxCacheBytes = 16 * 1024 * 1024;
    [JsonIgnore] public string DataUri => "data:" + Id3Codec.ArtworkMime(Data) + ";base64," + Convert.ToBase64String(Data);
    public bool Matches(Track track) => !track.Origin.Protected && MediaKey == Metadata.MediaKey(track) && Data is { Length: > 0 and <= MaxBytes };
    public void Validate(Track track)
    {
        if (!Enum.IsDefined(Kind) || !Matches(track)) throw new InvalidOperationException("Artwork does not belong to this available recording.");
        Id3Codec.ArtworkMime(Data);
    }
    public static byte[]? Embedded(Id3Document document)
    {
        foreach (var picture in document.Artwork.OrderBy(x => x.PictureType == 3 ? 0 : 1))
        {
            try { Id3Codec.ArtworkMime(picture.Data); return picture.Data; }
            catch (ArgumentException) { }
        }
        return null;
    }
}
