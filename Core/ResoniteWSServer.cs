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

        // When the factory hands every session the same media service (Linux),
        // that instance is owned by the host and must outlive any single
        // connection — sessions must not dispose it. Null when each session gets
        // its own service (Windows/macOS).
        public IMediaService SharedMediaService { get; set; }

        protected override TcpSession CreateSession() { return new ResoniteWSSession(this); }

        // Fan a per-session action out to every live client. The shared media
        // service uses this to push updates from its single poll loop instead
        // of one poll loop per connection.
        public void ForEachSession(Action<ResoniteWSSession> action)
        {
            foreach (var session in Sessions.Values)
                if (session is ResoniteWSSession resonite)
                    action(resonite);
        }

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
