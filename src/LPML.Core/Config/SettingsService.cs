using LPML.Core.Utils;

namespace LPML.Core.Config;

/// <summary>设置持久化：只写根目录下的 config\launcher.json。</summary>
public sealed class SettingsService
{
    public LauncherSettings Settings { get; set; } = new();

    public event EventHandler<LauncherSettings>? SettingsChanged;

    public void Load(string path)
    {
        var settings = Json.Load<LauncherSettings>(path);
        if (settings is null)
        {
            Settings = new LauncherSettings { IsFirstRun = true };
            Save(path);
            return;
        }
        Settings = settings;
        Normalize();
    }

    public void Save(string path)
    {
        try
        {
            Json.Save(path, Settings);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Update(Action<LauncherSettings> mutate)
    {
        mutate(Settings);
        Normalize();
        SettingsChanged?.Invoke(this, Settings);
    }

    private void Normalize()
    {
        var d = Settings.Download;
        d.MaxConcurrentTasks = Math.Clamp(d.MaxConcurrentTasks, 1, 128);
        d.MaxChunksPerFile = Math.Clamp(d.MaxChunksPerFile, 1, 32);
        d.ChunkThresholdMb = Math.Clamp(d.ChunkThresholdMb, 0, 1024);
        d.RetryCount = Math.Clamp(d.RetryCount, 0, 10);
        d.AutoRetryRounds = Math.Clamp(d.AutoRetryRounds, 0, 20);
        d.AutoRetryDelaySeconds = Math.Clamp(d.AutoRetryDelaySeconds, 0, 3600);
        d.TimeoutSeconds = Math.Clamp(d.TimeoutSeconds, 5, 600);
        d.SpeedLimitKb = Math.Max(0, d.SpeedLimitKb);

        var t = Settings.Temp;
        t.MaxTempSizeMb = Math.Max(0, t.MaxTempSizeMb);
        t.SingleFileTempLimitMb = Math.Max(0, t.SingleFileTempLimitMb);
        t.MaxCacheSizeMb = Math.Max(0, t.MaxCacheSizeMb);

        var j = Settings.Java;
        j.MinMemoryMb = Math.Clamp(j.MinMemoryMb, 128, 65536);
        j.MaxMemoryMb = Math.Clamp(j.MaxMemoryMb, 256, 262144);
        if (j.MaxMemoryMb < j.MinMemoryMb) j.MaxMemoryMb = j.MinMemoryMb;
        j.AutoMemoryRatio = Math.Clamp(j.AutoMemoryRatio, 0.05, 0.9);

        var a = Settings.Appearance;
        a.BackdropOpacity = Math.Clamp(a.BackdropOpacity, 0.1, 1.0);
        a.BackgroundBlurRadius = Math.Clamp(a.BackgroundBlurRadius, 0, 100);
        a.AnimationDurationMs = Math.Clamp(a.AnimationDurationMs, 0, 2000);
        a.HoverScaleAmount = Math.Clamp(a.HoverScaleAmount, 0, 0.2);
        a.CornerRadius = Math.Clamp(a.CornerRadius, 0, 32);
        a.FontScale = Math.Clamp(a.FontScale, 0.8, 1.6);
    }
}
