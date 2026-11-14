using ChannelWave.Core;

namespace ChannelWave.UI;

public sealed record HomeMusicAction(Track Track, string Action);

public sealed record MusicCreditDestination(long Account, string Kind, string Key, string Label);
