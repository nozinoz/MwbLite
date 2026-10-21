// MwbLite: minimal file logger replacing PowerToys' ManagedCommon.Logger and the Windows Event Log
// writes. One file per process role, capped at MaxBytes with a single ".1" rollover.
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace MouseWithoutBorders.Lite
{
    internal static class LiteLog
    {
        private const long MaxBytes = 4 * 1024 * 1024;
        private static readonly Lock FileLock = new();
        private static string logFile;

        internal static string LogFile => logFile;

        internal static void Initialize(string directory, string baseName)
        {
            try
            {
                Directory.CreateDirectory(directory);
                logFile = Path.Combine(directory, baseName + ".log");
            }
            catch (Exception e)
            {
                Debug.WriteLine("LiteLog: cannot create " + directory + ": " + e.Message);
                logFile = null;
            }
        }

        internal static void Write(string source, string message)
        {
            string file = logFile;
            if (file == null)
            {
                Debug.WriteLine(message);
                return;
            }

            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}: {3}\r\n",
                DateTime.Now,
                Environment.ProcessId,
                source,
                message);

            lock (FileLock)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Exists && info.Length > MaxBytes)
                    {
                        File.Move(file, file + ".1", overwrite: true);
                    }

                    File.AppendAllText(file, line, Encoding.UTF8);
                }
                catch (Exception e)
                {
                    Debug.WriteLine("LiteLog: " + e.Message);
                }
            }
        }
    }
}
