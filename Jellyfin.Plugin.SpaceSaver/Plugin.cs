using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Jellyfin.Plugin.SpaceSaver;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths, serializer) => Instance = this;
    public static Plugin? Instance { get; private set; }
    public override string Name => "Space Saver";
    public override string Description => "Convert library videos to HEVC with Intel GPU acceleration and verified track preservation.";
    public override Guid Id => Guid.Parse("e6f799fe-5693-4b68-95af-a7010949adeb");
    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new() { Name = "SpaceSaver", EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html", EnableInMainMenu = true },
        new() { Name = "SpaceSaver.js", EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.js" }
    ];
}

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string Backend { get; set; } = "qsv";
    public string Device { get; set; } = "/dev/dri/renderD128";
    public int Quality { get; set; } = 24;
    public bool HardwareDecode { get; set; } = true;
    public int MinimumSavingsPercent { get; set; } = 5;
}

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
    {
        services.AddSingleton<MediaCatalog>();
        services.AddSingleton<ProcessRunner>();
        services.AddSingleton<ConversionService>();
        services.AddSingleton<IHostedService>(provider => provider.GetRequiredService<ConversionService>());
    }
}
