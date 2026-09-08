using System.Text.Json;

namespace ServerMap.Configuration;

public sealed class ServerMapConfig
{
    public bool Enabled { get; set; } = true;
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 5080;
    public string Token { get; set; } = "";
    public bool Enable2D { get; set; } = true;
    public bool Enable3D { get; set; } = false;
    public int RenderThreads { get; set; } = 1;
    public int ChunkCacheSize { get; set; } = 512;
    public int MeshTileSize { get; set; } = 128;
    public int Lod { get; set; } = 1;
    // 3D meshes must include caves, buildings and suspended structures.  The
    // 2D renderer still uses height maps for its fast top-down pass.
    public bool SurfaceOnly { get; set; } = false;
    public int ScanAboveTerrain { get; set; } = 96;
    public int UndergroundMinY { get; set; } = 0;
    // Map snapshots can be written while the game world is at night.  Keep a
    // modest viewer-only floor so terrain remains legible without replacing
    // the saved colored block-light information.
    public float MapAmbientLight { get; set; } = .28f;
    public int PlayerUpdateMs { get; set; } = 1000;
    public bool PublicPlayers { get; set; } = true;
    public int MaxPoisPerPlayer { get; set; } = 10;
    public string ClientAssetsPath { get; set; } = "";
    // Directory of rendered PNG avatar layers.
    public string AvatarAssetsPath { get; set; } = "";

    public void ApplyFrom(ServerMapConfig value)
    {
        Enabled = value.Enabled; BindAddress = value.BindAddress; Port = value.Port; Token = value.Token;
        Enable2D = value.Enable2D; Enable3D = false; RenderThreads = value.RenderThreads; ChunkCacheSize = value.ChunkCacheSize;
        MeshTileSize = value.MeshTileSize; Lod = value.Lod; SurfaceOnly = value.SurfaceOnly; ScanAboveTerrain = value.ScanAboveTerrain;
        UndergroundMinY = value.UndergroundMinY; MapAmbientLight = value.MapAmbientLight; PlayerUpdateMs = value.PlayerUpdateMs;
        PublicPlayers = value.PublicPlayers; MaxPoisPerPlayer = value.MaxPoisPerPlayer; ClientAssetsPath = value.ClientAssetsPath;
        AvatarAssetsPath = value.AvatarAssetsPath;
    }

    public static bool TryReload(string path, ServerMapConfig target, out string error)
    {
        try
        {
            var value = JsonSerializer.Deserialize<ServerMapConfig>(File.ReadAllText(path));
            if (value == null) { error = "Configuration is empty."; return false; }
            value.Enable3D = false; target.ApplyFrom(value);
            error = ""; return true;
        }
        catch (Exception ex) { error = ex.Message; return false; }
    }

    public static ServerMapConfig Load(string path, Action<string> log)
    {
        try
        {
            if (File.Exists(path))
            {
                var value = JsonSerializer.Deserialize<ServerMapConfig>(File.ReadAllText(path));
                if (value != null)
                {
                    value.Enable3D = false;
                    File.WriteAllText(path, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
                    return value;
                }
            }
        }
        catch (Exception ex) { log($"Failed to read config: {ex.Message}"); }
        var config = new ServerMapConfig();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return config;
    }
}
