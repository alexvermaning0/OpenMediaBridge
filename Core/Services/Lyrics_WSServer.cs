using NetCoreServer;
using System;

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

        public new bool Start()
        {
            try
            {
                Console.WriteLine($"[DEBUG] LyricsWSServer attempting to bind to {Address}:{Port}");
                bool result = base.Start();
                Console.WriteLine($"[DEBUG] LyricsWSServer.Start() returned: {result}");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] LyricsWSServer.Start() exception: {ex.Message}");
                Console.WriteLine($"[DEBUG] Stack trace: {ex.StackTrace}");
                throw;
            }
        }
    }
}
