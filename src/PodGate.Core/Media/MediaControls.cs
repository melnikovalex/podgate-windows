using Windows.Media.Control;

namespace PodGate.Core.Media;

/// <summary>
/// Pausing playback through Windows' own media controls (plan F14). Per user session, so this belongs
/// in the app and never in the service. Best effort by design: a player that ignores the system media
/// controls cannot be paused this way, and that is documented rather than worked around.
/// </summary>
public static class MediaControls
{
    public static async Task<IReadOnlyList<string>> PausePlayingAsync(CancellationToken cancellationToken = default)
    {
        var paused = new List<string>();
        try
        {
            GlobalSystemMediaTransportControlsSessionManager manager =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);

            foreach (GlobalSystemMediaTransportControlsSession session in manager.GetSessions())
            {
                if (session.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing) continue;
                if (await session.TryPauseAsync().AsTask(cancellationToken)) paused.Add(session.SourceAppUserModelId);
            }
        }
        catch (Exception)
        {
            // Never let the media stack stop a release.
        }
        return paused;
    }

    /// <summary>
    /// Starts again exactly what PodGate paused, and nothing else: a pod back in the ear must not start
    /// music the user paused themselves, or a video in some other window.
    /// </summary>
    public static async Task ResumeAsync(IReadOnlyList<string> apps, CancellationToken cancellationToken = default)
    {
        if (apps.Count == 0) return;
        try
        {
            GlobalSystemMediaTransportControlsSessionManager manager =
                await GlobalSystemMediaTransportControlsSessionManager.RequestAsync().AsTask(cancellationToken);

            foreach (GlobalSystemMediaTransportControlsSession session in manager.GetSessions())
            {
                if (!apps.Contains(session.SourceAppUserModelId)) continue;
                if (session.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused)
                {
                    await session.TryPlayAsync().AsTask(cancellationToken);
                }
            }
        }
        catch (Exception)
        {
            // Same rule as pausing: the media stack must never break anything else.
        }
    }
}
