using System.Globalization;
using Jellyfin.Plugin.QuasiTV.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.QuasiTV;

/// <summary>Channel Free (formerly Cable Package): auto-generated live-TV channels served to Jellyfin Live TV. (Internal names stay "QuasiTV" so settings carry over.)</summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginGuid = "5490ddb4-bf71-48ab-a53c-09045405062e";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;

        var existingInstall = !string.IsNullOrWhiteSpace(Configuration.StreamToken);

        // First run: create the secret token that protects the M3U / guide / stream URLs.
        if (!existingInstall)
        {
            Configuration.StreamToken = Guid.NewGuid().ToString("N");
            Configuration.NamesVersion = 1;
            SaveConfiguration();
        }

        // 0.9.2 gave the built-in channels generic names. Existing installs keep the names they already had
        // (and keep their lineup — the starter-lineup pick is only for fresh installs).
        if (Configuration.NamesVersion < 1)
        {
            Configuration.StarterLineupDone = true;
            foreach (var (key, oldName) in Channels.ChannelRules.LegacyNames)
            {
                var o = Configuration.Overrides.FirstOrDefault(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase));
                if (o is null)
                {
                    o = new ChannelOverride { Key = key };
                    Configuration.Overrides.Add(o);
                }

                o.Name ??= oldName;
            }

            Configuration.NamesVersion = 1;
            SaveConfiguration();
        }

        // Turning the web/LG down-arrow banner on or off takes effect right away.
        var webPath = applicationPaths.WebPath;
        var guideSetup = GuideSetup(Configuration);
        ConfigurationChanged += (_, cfg) =>
        {
            if (cfg is PluginConfiguration c)
            {
                Channels.Startup.Apply(webPath, c.DownArrowBanner, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

                // Guide channel switched on/off or renumbered: Jellyfin has to re-read the channel list.
                var setup = GuideSetup(c);
                if (!string.Equals(setup, guideSetup, StringComparison.Ordinal))
                {
                    guideSetup = setup;
                    Channels.GuideRefresher.Request();
                }
            }
        };
    }

    public static Plugin? Instance { get; private set; }

    private static string GuideSetup(PluginConfiguration c)
        => string.Format(CultureInfo.InvariantCulture, "{0}|{1}", c.GuideEnabled, c.GuideNumber);

    public override string Name => "Channel Free";

    public override Guid Id => Guid.Parse(PluginGuid);

    public override string Description => "Live-TV channels built from your own library. No contract, no hidden fees.";

    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = Name,
                EmbeddedResourcePath = string.Format(CultureInfo.InvariantCulture, "{0}.Configuration.configPage.html", GetType().Namespace)
            }
        };
    }
}
