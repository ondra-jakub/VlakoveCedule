namespace VlakoveCedule.Base;

/// <summary>Injekce adresáře aplikace pro logy a stav (flow.log/crash.log/state.json).
/// Výchozí chování zachovává dnešní desktop cestu ApplicationData/VlakoveCedule;
/// MAUI má povinnost jednou při startu zavolat <see cref="Configure"/> (FileSystem.AppDataDirectory).</summary>
public static class AppPaths
{
    private static string? _dir;

    /// <summary>Nastaví základní adresář app dat; volá se jednou při startu aplikace.</summary>
    public static void Configure(string dir) => _dir = dir;

    /// <summary>Základní adresář app dat (default = ApplicationData/VlakoveCedule).</summary>
    public static string BaseDir =>
        _dir ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VlakoveCedule");
}