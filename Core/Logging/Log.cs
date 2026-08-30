using System;
using System.Collections.Generic;
using System.IO;

namespace OpenMediaBridge.Logging
{
    public enum LogLevel { Debug, Info, Warning, Error }

    // One log sink for the whole app. Every logging path — startup, MPRIS,
    // lyrics, fetchers — funnels through here, so the log file and the on-screen
    // debug pane show the same stream. It:
    //   * appends to a single file (rotated to .old once per launch),
    //   * keeps the recent lines the TUI debug pane renders,
    //   * echoes to the console until the TUI takes over the screen.
    public static class Log
    {
        private static readonly object _gate = new object();
        private static readonly Queue<string> _recent = new Queue<string>();
        private const int RecentMax = 50;

        private static string _file = "";

        // On until the TUI starts painting; afterwards the pane is the live view
        // and raw console writes would corrupt the frame. Flipped by the TUI.
        public static bool EchoToConsole = true;

        // Point the file sink at <dir>/openmediabridge.log, keeping the previous
        // run's log as openmediabridge.log.old (only ever two files).
        public static void Init(string dir)
        {
            lock (_gate)
            {
                try
                {
                    Directory.CreateDirectory(dir);
                    var path = Path.Combine(dir, "openmediabridge.log");
                    var old = Path.Combine(dir, "openmediabridge.log.old");
                    if (File.Exists(path))
                    {
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    _file = path;
                }
                catch { _file = ""; }
            }
        }

        // Oldest-first snapshot of the recent lines, for the TUI pane.
        public static IReadOnlyList<string> Recent()
        {
            lock (_gate) return new List<string>(_recent);
        }

        public static void Debug(string message) => Write(LogLevel.Debug, message);
        public static void Info(string message) => Write(LogLevel.Info, message);
        public static void Warning(string message) => Write(LogLevel.Warning, message);
        public static void Error(string message) => Write(LogLevel.Error, message);

        private static void Write(LogLevel level, string message)
        {
            var now = DateTime.Now;
            var paneLine = $"[{now:HH:mm:ss}] {message}";
            var fileLine = $"[{now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}";

            bool echo;
            lock (_gate)
            {
                _recent.Enqueue(paneLine);
                while (_recent.Count > RecentMax) _recent.Dequeue();
                if (_file.Length > 0)
                {
                    try { File.AppendAllText(_file, fileLine + Environment.NewLine); }
                    catch { }
                }
                echo = EchoToConsole;
            }

            if (echo)
            {
                if (level >= LogLevel.Warning) Console.Error.WriteLine(message);
                else Console.WriteLine(message);
            }
        }
    }
}
