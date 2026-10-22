using System.IO;
using System.Text.Json;
using VlakoveCedule.Base;

namespace VlakoveCedule.Core.Services;

/// <summary>Perzistence stavu aplikace (naposledy otevřená stanice) – AppPaths.BaseDir/state.json.</summary>
public sealed class AppStateStore
{
    public sealed class AppState
    {
        public string? Station { get; set; }
    }

    private static string GetAppData()
    {
        var dir = AppPaths.BaseDir;
        Directory.CreateDirectory(dir);
        return dir;
    }

    public AppState? Load()
    {
        try
        {
            var f = Path.Combine(GetAppData(), "state.json");
            if (!File.Exists(f)) return null;
            return JsonSerializer.Deserialize<AppState>(File.ReadAllText(f));
        }
        catch { return null; }
    }

    public void Save(AppState state)
    {
        try
        {
            var f = Path.Combine(GetAppData(), "state.json");
            File.WriteAllText(f, JsonSerializer.Serialize(state));
        }
        catch { }
    }
}
