namespace ChannelWave.Core;

public sealed record AudioOutput(string Id, string Name, bool Remote = false, bool Bluetooth = false)
{
    public static AudioOutput Default { get; } = new("", "System default");
}

public sealed record AudioOutputs(AudioOutput Active, AudioOutput[] Devices, string? Picker = null, string? Help = null,
    bool GroupAndroidOutputs = false)
{
    public static AudioOutputs SystemOnly { get; } = new(AudioOutput.Default, [], Help: "Change the output in your device’s sound settings.");
}

public interface IAudioRouting
{
    Task<AudioOutputs> GetOutputsAsync(CancellationToken ct);
    Task SelectOutputAsync(string? id, double position, bool playing, CancellationToken ct);
    async Task<AudioOutput> GetActiveOutputAsync(CancellationToken ct) => (await GetOutputsAsync(ct)).Active;
}

public sealed partial class PlaybackCoordinator
{
    private CancellationTokenSource? routeSelection;
    public AudioOutput ActiveOutput { get; private set; } = AudioOutput.Default;
    public event Action? OutputChanged;

    public async Task<AudioOutputs> GetOutputsAsync(CancellationToken ct = default)
    {
        if (engine is not IAudioRouting routing) return AudioOutputs.SystemOnly;
        await commands.WaitAsync(ct);
        try { return await RefreshOutputsAsync(routing, ct); }
        finally { commands.Release(); }
    }

    public async Task SelectOutputAsync(string? id, CancellationToken ct = default, bool stopCasting = false)
    {
        if (engine is not IAudioRouting routing) return;
        var requested = Volatile.Read(ref generation);
        using var transfer = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await commands.WaitAsync(ct);
        try
        {
            lock (selectionGate)
            {
                if (requested != generation || IsPreparing)
                    throw new InvalidOperationException("Wait for the current song to finish preparing, then choose an output.");
                routeSelection = transfer;
            }
            var state = CanResume ? await engine.StateAsync(transfer.Token) : null;
            if (state is not null) RecordObservation(state);
            recorder.Break();
            try
            {
                await routing.SelectOutputAsync(id, state?.Position ?? Queue.Position,
                    !stopCasting && state is { Paused: false, Ended: false, Error: null }, transfer.Token);
                if (requested != Volatile.Read(ref generation))
                {
                    await engine.PauseAsync();
                    return;
                }
            }
            finally
            {
                if (CanResume && requested == Volatile.Read(ref generation) &&
                    await engine.StateAsync(CancellationToken.None) is { } updated)
                {
                    IsPlaying = !updated.Paused && !updated.Ended && updated.Error is null;
                    if (stopCasting) completionArmed = false;
                    RecordObservation(updated);
                }
            }
            await RefreshOutputsAsync(routing, ct);
        }
        finally
        {
            lock (selectionGate)
                if (ReferenceEquals(routeSelection, transfer)) routeSelection = null;
            commands.Release();
            NotifyChanged();
        }
    }

    private async Task<AudioOutputs> RefreshOutputsAsync(IAudioRouting routing, CancellationToken ct)
    {
        var outputs = await routing.GetOutputsAsync(ct);
        SetActiveOutput(outputs.Active);
        return outputs;
    }

    private void SetActiveOutput(AudioOutput active)
    {
        if (ActiveOutput != active)
        {
            ActiveOutput = active;
            OutputChanged?.Invoke();
        }
    }

    private async Task ResetOutputAsync()
    {
        if (engine is not IAudioRouting routing) return;
        try
        {
            await routing.SelectOutputAsync(AudioOutput.Default.Id, 0, false, CancellationToken.None);
        }
        finally
        {
            SetActiveOutput(AudioOutput.Default);
        }
    }
}
