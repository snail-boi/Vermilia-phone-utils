using System.Diagnostics;
using System.IO;

namespace phone_utils
{
    internal class Debugger
    {
        // Define the log directory
        private static readonly string logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Snail"
        );

        private const int MaxLogFiles = 5; // Including latest.log + 4 rotated logs

        // Log file paths
        private static readonly string latestLogPath = Path.Combine(logDirectory, "latest.log");

        private static bool rotated = false; // Prevent multiple rotations per session

        public static void show(string message)
        {
            if (MainWindow.debugmode)
            {
                // Rotate logs only once per session
                if (!rotated)
                {
                    RotateLogs();
                    rotated = true;
                }

                string logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} - {message}";
                Debug.WriteLine(logEntry);
                WriteToLogFile(logEntry);
            }
        }

        private static void WriteToLogFile(string message)
        {
            try
            {
                if (!Directory.Exists(logDirectory))
                    Directory.CreateDirectory(logDirectory);

                File.AppendAllText(latestLogPath, message + Environment.NewLine);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Logging Error] {ex.Message}");
            }
        }

        private static void RotateLogs()
        {
            try
            {
                if (!Directory.Exists(logDirectory))
                    Directory.CreateDirectory(logDirectory);

                // Delete the oldest log (debug4.log)
                string oldestLog = Path.Combine(logDirectory, $"debug{MaxLogFiles - 1}.log");
                if (File.Exists(oldestLog))
                    File.Delete(oldestLog);

                // Shift debug3.log → debug4.log, ..., debug1.log → debug2.log
                for (int i = MaxLogFiles - 2; i >= 1; i--)
                {
                    string src = Path.Combine(logDirectory, $"debug{i}.log");
                    string dest = Path.Combine(logDirectory, $"debug{i + 1}.log");

                    if (File.Exists(src))
                        File.Move(src, dest);
                }

                // Move latest.log → debug1.log
                string firstBackup = Path.Combine(logDirectory, "debug1.log");
                if (File.Exists(latestLogPath))
                    File.Move(latestLogPath, firstBackup);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[Log Rotation Error] {ex.Message}");
            }
        }
    }
}
