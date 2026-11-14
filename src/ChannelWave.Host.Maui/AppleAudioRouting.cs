#if IOS || MACCATALYST
using AVFoundation;
using AVKit;
using ChannelWave.Core;
using CoreGraphics;
using Foundation;
using UIKit;

namespace ChannelWave.Host.Maui;

public sealed partial class NativeAudio : IAudioRouting, IAudioNotifications
{
    private NSObject? routeObserver;
    private Func<long, string, double, Task>? notifyRoute;
    private long routeEpoch;

    private void ConfigureAppleAudio()
    {
        var session = AVAudioSession.SharedInstance();
        if (!session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionMode.Default,
                AVAudioSessionRouteSharingPolicy.LongFormAudio, default, out _))
            session.SetCategory(AVAudioSessionCategory.Playback);
        routeObserver ??= AVAudioSession.Notifications.ObserveRouteChange((sender, change) =>
        {
            var lost = change.Reason == AVAudioSessionRouteChangeReason.OldDeviceUnavailable;
            if (lost) player?.Pause();
            _ = notifyRoute?.Invoke(routeEpoch, lost ? "output-lost" : "output-changed", 0);
        });
    }

    public Task ObserveAsync(long playbackId, Func<long, string, double, Task> notify, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        routeEpoch = playbackId;
        notifyRoute = notify;
        return Task.CompletedTask;
    }

    public Task<AudioOutputs> GetOutputsAsync(CancellationToken ct) => MainThread.InvokeOnMainThreadAsync(() =>
    {
        ct.ThrowIfCancellationRequested();
        var ports = AVAudioSession.SharedInstance().CurrentRoute.Outputs;
        var active = ports.Length == 0 ? AudioOutput.Default : new AudioOutput(
            ports[0].UID, string.Join(", ", ports.Select(port => port.PortName)),
            ports.Any(port => port.PortType == AVAudioSession.PortAirPlay));
        return new AudioOutputs(active, [], "Open AirPlay and system outputs",
            "Choose a nearby AirPlay device or change your current output using Apple’s route picker.");
    });

    public Task SelectOutputAsync(string? id, double position, bool playing, CancellationToken ct)
    {
        if (id is not null) return Task.CompletedTask; // The system owns Apple’s selected route.
        return MainThread.InvokeOnMainThreadAsync(() =>
        {
            ct.ThrowIfCancellationRequested();
            ConfigureAppleAudio();
            var presenter = Platform.GetCurrentUIViewController()
                ?? throw new InvalidOperationException("Open ChannelWave before choosing an audio output.");
            var controller = new UIViewController { ModalPresentationStyle = UIModalPresentationStyle.FormSheet };
            controller.PreferredContentSize = new CGSize(320, 190);
            var view = controller.View!;
            view.BackgroundColor = UIColor.SystemBackground;
            var title = new UILabel { Text = "AirPlay and audio outputs", TextAlignment = UITextAlignment.Center };
            var routes = new AVRoutePickerView { TintColor = UIColor.Label, ActiveTintColor = UIColor.SystemGreen };
            var done = UIButton.FromType(UIButtonType.System);
            done.SetTitle("Done", UIControlState.Normal);
            done.TouchUpInside += (_, _) => controller.DismissViewController(true, null);
            var stack = new UIStackView([title, routes, done])
            {
                Axis = UILayoutConstraintAxis.Vertical,
                Alignment = UIStackViewAlignment.Center,
                Spacing = 8,
                TranslatesAutoresizingMaskIntoConstraints = false
            };
            view.AddSubview(stack);
            NSLayoutConstraint.ActivateConstraints([
                stack.CenterXAnchor.ConstraintEqualTo(view.CenterXAnchor),
                stack.CenterYAnchor.ConstraintEqualTo(view.CenterYAnchor),
                routes.WidthAnchor.ConstraintEqualTo(48),
                routes.HeightAnchor.ConstraintEqualTo(48)
            ]);
            presenter.PresentViewController(controller, true, null);
        });
    }
}
#endif
