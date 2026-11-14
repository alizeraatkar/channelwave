namespace ChannelWave.UI;

public enum PlaylistAction { Create, Rename, Duplicate, Delete, Add, Remove, Move, Clear }

public sealed record PlaylistEdit(PlaylistAction Action, Guid PlaylistId = default, string? Name = null,
    Guid[]? Tracks = null, Guid TrackId = default, int Direction = 0);
