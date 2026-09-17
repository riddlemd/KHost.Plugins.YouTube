using KHost.Abstractions.Models.Plugins;
using KHost.Abstractions.Models;
using System.Text.Json;

namespace KHost.Plugins.YouTube.Tests;

/// <summary>The manifest is read by the host, not this assembly, so an unparseable setting fails
/// silently at load time, not build time: no row for the plugin, see <see cref="YouTubeSettings"/>.</summary>
public class ManifestTests
{
    private const string PluginManifestFileName = "manifest.json";

    private static readonly string ManifestPath = Path.Combine(AppContext.BaseDirectory, PluginManifestFileName);

    [Fact]
    public void Manifest_ParsesTheWayTheHostParsesIt()
    {
        var manifest = Read();

        Assert.NotEqual(Guid.Empty, manifest.Id);
        Assert.Equal(PluginApi.CurrentVersion, manifest.ApiVersion);
        Assert.Equal("KHost.Plugins.YouTube.dll", manifest.EntryAssembly);
        Assert.NotEmpty(manifest.Settings);
    }

    [Fact]
    public void Manifest_EverySettingKeyBindsToAYouTubeSettingsProperty()
    {
        var properties = typeof(YouTubeSettings)
            .GetProperties()
            .Select(property => property.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var setting in Read().Settings)
            Assert.True(properties.Contains(setting.Key), $"Manifest setting '{setting.Key}' binds to nothing.");
    }

    private static PluginManifest Read()
        => JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(ManifestPath), JsonSerializerOptions.Web)!;
}
