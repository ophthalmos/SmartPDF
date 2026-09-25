using System.Text.Json;

namespace MozillaPDF.Classes;

/// <summary>Einstellungen als JSON unter %APPDATA%\MozillaPDF\settings.json – derzeit nur Lage, Größe und Zustand des Hauptfensters.</summary>
internal sealed class AppSettings
{
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public int WindowWidth { get; set; }     // 0 = noch nie gespeichert → Vorgabe aus dem Designer
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MozillaPDF", "settings.json");

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>Fehlt die Datei oder ist sie unlesbar, gelten die Vorgaben.</summary>
    public static AppSettings Load()
    {
        try { return File.Exists(SettingsPath) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new() : new(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return new(); }
    }

    /// <summary>Erst eine Nachbardatei schreiben, dann eintauschen – nie eine halbe Datei (Erfahrung aus PDFlight).</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!); // SettingsPath ist immer ein voller Dateipfad
            var temp = SettingsPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, SerializerOptions));
            File.Move(temp, SettingsPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } // Speichern darf das Programm nie blockieren
    }
}
