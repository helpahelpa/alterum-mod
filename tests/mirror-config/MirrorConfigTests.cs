using System;
using System.Collections.Generic;
using System.IO;
using AlterumModelSwap;
using BepInEx.Configuration;
using Xunit;

namespace AlterumModelSwap.MirrorConfigTests;

// These tests use a real BepInEx ConfigFile: the migration depends on its parser, its orphan
// handling and its save path. No Unity, BepInEx preloader or game process is started.
public class MirrorConfigTests : IDisposable
{
    private readonly string _dir;
    private readonly List<string> _log = new List<string>();

    public MirrorConfigTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "alterum-mirror-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }

    private ConfigEntry<bool> Bind(string path, out ConfigFile file)
    {
        file = new ConfigFile(path, saveOnInit: true);
        return MirrorConfig.BindReflectLocalPlayer(file, _log.Add);
    }

    [Fact]
    public void FreshConfig_DefaultsToSelfReflectionOn()
    {
        var path = Path.Combine(_dir, "fresh.cfg");
        var entry = Bind(path, out var file);

        Assert.True(entry.Value);
        Assert.True(file.ContainsKey(new ConfigDefinition("Mirror", "ReflectLocalPlayer")));
        Assert.False(file.ContainsKey(new ConfigDefinition("Mirror", "ShowLocalPlayer")));

        var text = File.ReadAllText(path);
        Assert.Contains("ReflectLocalPlayer = true", text);
        Assert.DoesNotContain("ShowLocalPlayer", text);
        Assert.Empty(_log);
    }

    [Fact]
    public void PreFixFalse_IsRetiredAndReflectionDefaultsOn()
    {
        var path = Write("pre-fix.cfg",
            "## Settings file was created by plugin Alterum Model Swap (host) v2.0.0\n" +
            "\n[Mirror]\n\n" +
            "## Only in the Lobby scene.\n# Setting type: Boolean\n# Default value: true\nLobbyOnly = true\n\n" +
            "## Does not work - the game never draws your own body.\n" +
            "# Setting type: Boolean\n# Default value: false\nShowLocalPlayer = false\n");

        var entry = Bind(path, out var file);

        Assert.True(entry.Value);
        Assert.False(file.ContainsKey(new ConfigDefinition("Mirror", "ShowLocalPlayer")));
        Assert.True(file.ContainsKey(new ConfigDefinition("Mirror", "ReflectLocalPlayer")));

        var text = File.ReadAllText(path);
        Assert.Contains("ReflectLocalPlayer = true", text);
        Assert.DoesNotContain("ShowLocalPlayer", text);
        // An unbound key that happened to sit in the same file must survive the migration save.
        Assert.Contains("LobbyOnly = true", text);
        Assert.Contains(_log, line => line.Contains("ShowLocalPlayer") && line.Contains("ReflectLocalPlayer"));
    }

    [Fact]
    public void NewFalse_OptOutIsHonored()
    {
        var path = Write("opt-out.cfg", "[Mirror]\n\nReflectLocalPlayer = false\n");
        var entry = Bind(path, out var file);

        Assert.False(entry.Value);
        Assert.True(file.ContainsKey(new ConfigDefinition("Mirror", "ReflectLocalPlayer")));

        var text = File.ReadAllText(path);
        Assert.Contains("ReflectLocalPlayer = false", text);
        Assert.DoesNotContain("ShowLocalPlayer", text);
        Assert.Empty(_log);
    }

    [Fact]
    public void NewKeyWins_WhenObsoleteKeyIsAlsoPresent()
    {
        var path = Write("both.cfg", "[Mirror]\n\nShowLocalPlayer = true\nReflectLocalPlayer = false\n");
        var entry = Bind(path, out var file);

        Assert.False(entry.Value);
        Assert.False(file.ContainsKey(new ConfigDefinition("Mirror", "ShowLocalPlayer")));

        var text = File.ReadAllText(path);
        Assert.Contains("ReflectLocalPlayer = false", text);
        Assert.DoesNotContain("ShowLocalPlayer", text);
        Assert.Contains(_log, line => line.Contains("ShowLocalPlayer") && line.Contains("ReflectLocalPlayer"));
    }
}
