using System;
using NetCoreServer;
using OpenMediaBridge.Services;

namespace OpenMediaBridge
{
    public class ResoniteWSServer : WsServer
    {
        public static int ConnectedCount = 0;

        public ResoniteWSServer(string address, int port) : base(address, port) { }
        public Config Config { get; set; }

        public Func<ResoniteWSSession, ResoniteWSServer, IMediaService> MediaServiceFactory { get; set; }

        protected override TcpSession CreateSession() { return new ResoniteWSSession(this); }

        public new bool Start()
        {
            try
            {
                Console.WriteLine($"[DEBUG] ResoniteWSServer attempting to bind to {Address}:{Port}");
                bool result = base.Start();
                Console.WriteLine($"[DEBUG] ResoniteWSServer.Start() returned: {result}");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[DEBUG] ResoniteWSServer.Start() exception: {ex.Message}");
                Console.WriteLine($"[DEBUG] Stack trace: {ex.StackTrace}");
                throw;
            }
        }
    }
}
