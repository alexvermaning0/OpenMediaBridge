using NetCoreServer;

namespace OpenMediaBridge
{
    public class ResoniteWSServer : WsServer
    {
        public static int ConnectedCount = 0;

        public ResoniteWSServer(string address, int port) : base(address, port) { }
        public Config Config { get; set; }
        protected override TcpSession CreateSession() { return new ResoniteWSSession(this); }
    }
}
