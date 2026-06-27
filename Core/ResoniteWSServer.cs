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

        // Set by each platform's Program.cs so sessions can construct the right
        // IMediaService implementation (SMTC on Windows, MPRIS on Linux, ...).
        public Func<ResoniteWSSession, ResoniteWSServer, IMediaService> MediaServiceFactory { get; set; }

        protected override TcpSession CreateSession() { return new ResoniteWSSession(this); }
    }
}
