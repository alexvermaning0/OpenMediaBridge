namespace OpenMediaBridge.Services
{
    public class MediaProperties
    {
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string AlbumTitle { get; set; } = "";
        public string ArtUrl { get; set; } = "";
    }

    public class MediaPlaybackInfo
    {
        public bool IsPlaying { get; set; }
        public bool IsShuffleActive { get; set; }
        public string RepeatMode { get; set; } = "none"; // "none" | "track" | "playlist"
    }

    public class MediaTimelineInfo
    {
        public TimeSpan Position { get; set; }
        public TimeSpan Duration { get; set; }
    }

    public enum MediaControlType { Play, Pause, Stop, Skip, Previous }

    /// <summary>
    /// Platform-specific media session reader (SMTC on Windows, MPRIS/playerctl on Linux, etc).
    /// Lyrics_Service and ResoniteWSSession only talk to this interface, never to a platform API directly.
    /// </summary>
    public interface IMediaService
    {
        MediaProperties? CurrentMediaProperties { get; }
        bool HasActiveSession { get; }
        string CurrentSourceApp { get; }
        ResoniteWSSession WSSession { get; }
        ResoniteWSServer Server { get; }
        Config Config { get; }

        MediaPlaybackInfo GetPlaybackInfo();
        MediaTimelineInfo GetTimelineInfo();
        Task TryMediaControl(MediaControlType type);
        // Begin producing media updates for this service's WSSession. Platforms
        // that poll (Linux/macOS) start their loop here; event-driven platforms
        // (Windows) initialize in their constructor and may no-op this.
        void Start();
        void Dispose();
    }
}
