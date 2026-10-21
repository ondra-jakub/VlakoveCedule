using System.IO;

namespace VlakoveCedule;

/// <summary>Sdílený synchronizovaný writer pro diagnostické logy (flow.log / crash.log).</summary>
internal static class DiagLog
{
    private static readonly object Lock = new();
    private static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VlakoveCedule");

    public static void Append(string fileName, string line)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            lock (Lock)
                File.AppendAllText(Path.Combine(Dir, fileName), $"{DateTime.Now:HH:mm:ss.fff} {line}\n");
        }
        catch { }
    }
}