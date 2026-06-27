using NetCoreServer;

namespace OpenMediaBridge.Services
{
    public class LyricsWSServer : WsServer
    {
        public static int ConnectedCount = 0;

        public LyricsService LyricsService { get; private set; }

        public LyricsWSServer(string address, int port, LyricsService lyricsService) : base(address, port)
        {
            LyricsService = lyricsService;
        }
        protected override TcpSession CreateSession()
        {
            return new LyricsWSSession(this, LyricsService);
        }
    }
}
