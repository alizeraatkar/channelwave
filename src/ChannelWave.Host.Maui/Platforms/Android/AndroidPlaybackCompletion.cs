namespace ChannelWave.Host.Maui;

// Accessed on Android's main thread. Native Completion is authoritative even when
// timestamps disagree. Polling can also recover a lost event after observed playback
// stops at a known EOF. Pauses and interruptions invalidate this fallback.
internal sealed class AndroidPlaybackCompletion
{
    private long version;
    private bool armed;
    private bool observedPlaying;
    public bool Ended { get; private set; }

    public long Start()
    {
        Ended = false;
        observedPlaying = false;
        armed = true;
        return ++version;
    }

    public void Invalidate()
    {
        ++version;
        armed = false;
        observedPlaying = false;
    }

    public void ClearEnded() => Ended = false;

    public void ObserveState(bool playing, double position, double duration)
    {
        if (!armed) return;
        if (playing) { observedPlaying = true; return; }
        // Do not turn startup, buffering, unknown duration or a stop in the middle
        // of a recording into completion. Allow only native timestamp rounding.
        if (observedPlaying && double.IsFinite(position) && double.IsFinite(duration)
            && duration > 0 && position >= Math.Max(0, duration - 0.05))
            TryComplete(version);
    }

    public bool TryComplete(long token)
    {
        if (!armed || token != version) return false;
        armed = false;
        Ended = true;
        return true;
    }
}
