using ChannelWave.Core;

namespace ChannelWave.Host.Maui;

/// <summary>A connected receiver can temporarily have no media information, especially at EOF.</summary>
internal sealed class CastPlaybackState
{
    private string? expected;
    private AudioState? last;

    public void BeginLoad(string url) { expected = url; last = null; }
    public void Clear() { expected = null; last = null; }

    public AudioState? Observe(bool connected, string? contentId, AudioState? state)
    {
        if (!connected) return null;
        if (expected is not null && (contentId is null || contentId == expected) && state is { Error: not null })
            return last = (last ?? state) with { Paused = true, Ended = false, Buffering = false, Error = state.Error };
        // FINISHED may omit MediaInfo. Accept it only after observing this load,
        // so the preceding song's terminal status cannot finish a new selection.
        if (expected is not null && (contentId is null || contentId == expected) && last is not null && state?.Ended == true)
            return last = last with { Position = last.Duration, Paused = true, Ended = true, Buffering = false };
        if (expected is not null && contentId == expected && state is not null)
            return last = state;
        return (last ?? new AudioState(0, 0, 0, true, false, 1)) with
        {
            Paused = true, Ended = false, Buffering = expected is not null, Error = null
        };
    }
}
