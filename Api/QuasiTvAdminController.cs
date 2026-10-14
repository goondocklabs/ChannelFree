using Jellyfin.Plugin.QuasiTV.Channels;
using Jellyfin.Plugin.QuasiTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.QuasiTV.Api;

/// <summary>Channel editor API used by the plugin's dashboard page. Admins only.</summary>
[ApiController]
[Route("QuasiTV/admin")]
[Authorize(Policy = "RequiresElevation")]
public class QuasiTvAdminController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly ITunerHostManager _tuners;
    private readonly IListingsManager _listings;
    private readonly IConfigurationManager _serverConfig;

    public QuasiTvAdminController(ILibraryManager library, ITunerHostManager tuners, IListingsManager listings, IConfigurationManager serverConfig)
    {
        _library = library;
        _tuners = tuners;
        _listings = listings;
        _serverConfig = serverConfig;
    }

    private const string M3uPath = "/QuasiTV/channels.m3u";
    private const string GuidePath = "/QuasiTV/guide.xml";

    private static string BaseUrl => (string.IsNullOrWhiteSpace(Config.InternalUrl) ? "http://localhost:8096" : Config.InternalUrl).TrimEnd('/');

    private static string M3uUrl => BaseUrl + M3uPath + "?token=" + Config.StreamToken;

    private static string GuideUrl => BaseUrl + GuidePath + "?token=" + Config.StreamToken;

    private LiveTvOptions LiveTv => _serverConfig.GetConfiguration<LiveTvOptions>("livetv");

    private TunerHostInfo? OurTuner => LiveTv.TunerHosts.FirstOrDefault(t => (t.Url ?? string.Empty).Contains(M3uPath, StringComparison.OrdinalIgnoreCase));

    private ListingsProviderInfo? OurGuide => LiveTv.ListingProviders.FirstOrDefault(p => (p.Path ?? string.Empty).Contains(GuidePath, StringComparison.OrdinalIgnoreCase));

    /// <summary>Is Channel Free hooked up to Jellyfin Live TV (M3U tuner + XMLTV guide)?</summary>
    [HttpGet("livetv-status")]
    public IActionResult LiveTvStatus()
    {
        var tuner = OurTuner;
        var guide = OurGuide;
        return Ok(new
        {
            tuner = tuner is not null,
            guide = guide is not null,
            tunerCurrent = tuner is not null && string.Equals(tuner.Url, M3uUrl, StringComparison.OrdinalIgnoreCase),
            guideCurrent = guide is not null && string.Equals(guide.Path, GuideUrl, StringComparison.OrdinalIgnoreCase),
            m3uUrl = M3uUrl,
            guideUrl = GuideUrl
        });
    }

    /// <summary>One click: add (or fix) the M3U tuner and the XMLTV guide provider in Jellyfin Live TV.</summary>
    [HttpPost("connect-livetv")]
    public async Task<IActionResult> ConnectLiveTv()
    {
        var step = "tuner";
        try
        {
            var tuner = OurTuner;
            if (tuner is null)
            {
                await _tuners.SaveTunerHost(new TunerHostInfo
                {
                    Type = "m3u",
                    Url = M3uUrl,
                    FriendlyName = "Channel Free",
                    TunerCount = 0,
                    ImportFavoritesOnly = false
                }).ConfigureAwait(false);
            }
            else if (!string.Equals(tuner.Url, M3uUrl, StringComparison.OrdinalIgnoreCase))
            {
                tuner.Url = M3uUrl;
                await _tuners.SaveTunerHost(tuner).ConfigureAwait(false);
            }

            step = "guide";
            var guide = OurGuide;
            if (guide is null)
            {
                await _listings.SaveListingProvider(new ListingsProviderInfo
                {
                    Type = "xmltv",
                    Path = GuideUrl,
                    EnableAllTuners = true
                }, false, false).ConfigureAwait(false);
            }
            else if (!string.Equals(guide.Path, GuideUrl, StringComparison.OrdinalIgnoreCase))
            {
                guide.Path = GuideUrl;
                await _listings.SaveListingProvider(guide, false, false).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            return Ok(new
            {
                ok = false,
                step,
                error = ex.Message,
                hint = "Jellyfin couldn't open " + (step == "tuner" ? M3uUrl : GuideUrl) + " — check Internal Jellyfin URL in Setup & settings."
            });
        }

        GuideRefresher.Request();
        return Ok(new { ok = true });
    }

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    public sealed class ItemRequest
    {
        public Guid ItemId { get; set; }
    }

    public sealed class RankingRequest
    {
        public List<Guid> ItemIds { get; set; } = new();
    }

    public sealed class SettingsRequest
    {
        /// <summary>Weighted mix on/off.</summary>
        public bool? Weighted { get; set; }

        public bool? Enabled { get; set; }

        public string? Name { get; set; }

        public int? Number { get; set; }

        /// <summary>"shuffle" or "inorder".</summary>
        public string? Order { get; set; }

        /// <summary>Episodes in a row (1–6).</summary>
        public int? EpisodesInARow { get; set; }

        /// <summary>Era start year; 0 clears it.</summary>
        public int? YearFrom { get; set; }

        /// <summary>Era end year; 0 clears it.</summary>
        public int? YearTo { get; set; }

        /// <summary>"movies", "shows", "all", or "off".</summary>
        public string? EraAdd { get; set; }

        /// <summary>"MM-DD"; empty clears the season.</summary>
        public string? SeasonStart { get; set; }

        public string? SeasonEnd { get; set; }
    }

    public sealed class ShowBlockRequest
    {
        public Guid ItemId { get; set; }

        /// <summary>0 = use the channel default.</summary>
        public int Count { get; set; }
    }

    public sealed class GenreRequest
    {
        public string Genre { get; set; } = string.Empty;

        /// <summary>movies | shows | all (for include).</summary>
        public string Scope { get; set; } = "all";

        /// <summary>include | exclude | clear.</summary>
        public string Mode { get; set; } = "include";

        /// <summary>genre | tag | studio.</summary>
        public string Kind { get; set; } = "genre";
    }

    public sealed class CreateRequest
    {
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>All channels (on and off) for the editor list.</summary>
    [HttpGet("channels")]
    public IActionResult List()
    {
        var idx = ChannelScheduler.Index(_library);
        var live = ChannelScheduler.GetChannels(_library).ToDictionary(c => c.Def.Key, StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow;

        var list = ChannelRules.AllDefs(idx, Config)
            .OrderBy(d => d.Number)
            .Select(d =>
            {
                var pool = ChannelRules.Pool(d, idx, Config);
                live.TryGetValue(d.Key, out var ch);
                var cur = ch is null ? null : ChannelScheduler.Current(ch, now);
                return new
                {
                    key = d.Key,
                    name = d.Name,
                    number = d.Number,
                    kind = d.Kind.ToString(),
                    enabled = ChannelRules.IsEnabled(d, Config),
                    inSeason = ChannelRules.InSeason(ChannelRules.Override(Config, d.Key), DateTime.Now),
                    count = pool.Count,
                    now = cur is null ? null : Title(cur.Item),
                    nowEndsUtc = cur?.EndUtc
                };
            });

        return Ok(list);
    }

    /// <summary>Every genre (or keyword tag with ?kind=tag) in the library with how many movies / shows have it.</summary>
    [HttpGet("genres")]
    public IActionResult Genres([FromQuery] string? kind)
    {
        var idx = ChannelScheduler.Index(_library);
        var tags = string.Equals(kind, "tag", StringComparison.OrdinalIgnoreCase);
        var studios = string.Equals(kind, "studio", StringComparison.OrdinalIgnoreCase);
        Func<BaseItem, string[]?> field = tags ? i => i.Tags : studios ? i => i.Studios : i => i.Genres;

        var movies = idx.Movies.SelectMany(m => field(m) ?? Array.Empty<string>()).GroupBy(g => g, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var shows = idx.Series.SelectMany(x => field(x) ?? Array.Empty<string>()).GroupBy(g => g, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var videos = idx.MusicVideos.SelectMany(x => field(x) ?? Array.Empty<string>()).GroupBy(g => g, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var list = movies.Keys.Union(shows.Keys, StringComparer.OrdinalIgnoreCase).Union(videos.Keys, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Any(char.IsLetterOrDigit))
            .Where(g => !g.StartsWith("quasi:", StringComparison.OrdinalIgnoreCase) && !g.StartsWith("cable:", StringComparison.OrdinalIgnoreCase))
            .Select(g => new { name = g, movies = movies.GetValueOrDefault(g), shows = shows.GetValueOrDefault(g), musicVideos = videos.GetValueOrDefault(g) })
            .Where(g => !tags || g.movies + g.shows + g.musicVideos >= 2) // keywords: skip one-offs
            .OrderBy(g => g.name, StringComparer.OrdinalIgnoreCase);
        return Ok(list);
    }

    /// <summary>Include / exclude / clear a whole genre or keyword tag on a channel.</summary>
    [HttpPost("channels/{key}/genres")]
    public IActionResult Genre([FromRoute] string key, [FromBody] GenreRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Genre))
        {
            return BadRequest();
        }

        var o = Edit(key);
        var g = req.Genre.Trim();
        var kind = (req.Kind ?? "genre").ToLowerInvariant();
        var include = kind == "tag" ? o.IncludeTags : kind == "studio" ? o.IncludeStudios : o.IncludeGenres;
        var exclude = kind == "tag" ? o.ExcludeTags : kind == "studio" ? o.ExcludeStudios : o.ExcludeGenres;
        include.RemoveAll(e => string.Equals(ChannelRules.SplitGenre(e).Genre, g, StringComparison.OrdinalIgnoreCase));
        exclude.RemoveAll(e => string.Equals(e, g, StringComparison.OrdinalIgnoreCase));

        switch (req.Mode.ToLowerInvariant())
        {
            case "include":
                var scope = req.Scope.ToLowerInvariant() is "movies" or "shows" or "musicvideos" ? req.Scope.ToLowerInvariant() : "all";
                include.Add(scope + ":" + g);
                break;
            case "exclude":
                exclude.Add(g);
                break;
        }

        return Changed(key);
    }

    /// <summary>Everything on one channel + what's been removed.</summary>
    [HttpGet("channels/{key}")]
    public IActionResult Detail([FromRoute] string key)
    {
        var idx = ChannelScheduler.Index(_library);
        var def = FindDef(idx, key);
        if (def is null)
        {
            return NotFound();
        }

        var pool = ChannelRules.Pool(def, idx, Config);
        var o = ChannelRules.Override(Config, def.Key);

        var items = pool
            .Select(u => Describe(idx, u.Id, u.Reason))
            .Where(x => x is not null)
            .OrderBy(x => x!.name, StringComparer.OrdinalIgnoreCase);

        var collections = (o?.Added ?? new List<Guid>())
            .Where(id => idx.Collections.ContainsKey(id))
            .Select(id => Describe(idx, id, $"Collection · {idx.Collections[id].Members.Count} titles"))
            .Where(x => x is not null);

        var removed = (o?.Removed ?? new List<Guid>())
            .Select(id => Describe(idx, id, "Removed"))
            .Where(x => x is not null)
            .OrderBy(x => x!.name, StringComparer.OrdinalIgnoreCase);

        return Ok(new
        {
            key = def.Key,
            name = def.Name,
            number = def.Number,
            kind = def.Kind.ToString(),
            enabled = ChannelRules.IsEnabled(def, Config),
            inSeason = ChannelRules.InSeason(o, DateTime.Now),
            yearFrom = o?.YearFrom,
            yearTo = o?.YearTo,
            eraAdd = o?.EraAdd ?? "off",
            seasonStart = o?.SeasonStart,
            seasonEnd = o?.SeasonEnd,
            order = ChannelRules.InOrder(def, Config) ? "inorder" : "shuffle",
            inARow = o?.EpisodesInARow ?? 1,
            weighted = o?.Weighted ?? false,
            ranking = ChannelRules.EffectiveRanking(pool.Select(u => u.Id), o?.Ranking ?? new List<Guid>(), id => items.FirstOrDefault(x => x!.id == id.ToString("N"))?.name ?? string.Empty).Select(g => g.ToString("N")),
            showBlocks = (o?.ShowBlocks ?? new List<ShowBlock>()).ToDictionary(b => b.SeriesId.ToString("N"), b => b.Count),
            keepCredits = Config.KeepCredits.Select(g => g.ToString("N")),
            includeGenres = (o?.IncludeGenres ?? new List<string>()).Select(e => { var (sc, gn) = ChannelRules.SplitGenre(e); return new { genre = gn, scope = sc }; }),
            excludeGenres = o?.ExcludeGenres ?? new List<string>(),
            includeTags = (o?.IncludeTags ?? new List<string>()).Select(e => { var (sc, gn) = ChannelRules.SplitGenre(e); return new { genre = gn, scope = sc }; }),
            excludeTags = o?.ExcludeTags ?? new List<string>(),
            includeStudios = (o?.IncludeStudios ?? new List<string>()).Select(e => { var (sc, gn) = ChannelRules.SplitGenre(e); return new { genre = gn, scope = sc }; }),
            excludeStudios = o?.ExcludeStudios ?? new List<string>(),
            collections,
            items,
            removed
        });
    }

    [HttpPost("channels/{key}/add")]
    public IActionResult Add([FromRoute] string key, [FromBody] ItemRequest req)
    {
        var o = Edit(key);
        o.Removed.Remove(req.ItemId);
        if (!o.Added.Contains(req.ItemId))
        {
            o.Added.Add(req.ItemId);
        }

        return Changed(key);
    }

    [HttpPost("channels/{key}/remove")]
    public IActionResult Remove([FromRoute] string key, [FromBody] ItemRequest req)
    {
        var o = Edit(key);
        var wasAdded = o.Added.Remove(req.ItemId);

        // Taking a collection off just un-adds it (its titles leave with it).
        if (wasAdded && ChannelScheduler.Index(_library).Collections.ContainsKey(req.ItemId))
        {
            return Changed(key);
        }

        if (!o.Removed.Contains(req.ItemId))
        {
            o.Removed.Add(req.ItemId);
        }

        return Changed(key);
    }

    [HttpPost("channels/{key}/restore")]
    public IActionResult Restore([FromRoute] string key, [FromBody] ItemRequest req)
    {
        Edit(key).Removed.Remove(req.ItemId);
        return Changed(key);
    }

    [HttpPost("channels/{key}/settings")]
    public IActionResult Settings([FromRoute] string key, [FromBody] SettingsRequest req)
    {
        var o = Edit(key);
        if (req.Enabled.HasValue)
        {
            o.Enabled = req.Enabled;
        }

        if (req.Name is not null)
        {
            o.Name = string.IsNullOrWhiteSpace(req.Name) ? null : req.Name.Trim();
            var custom = Config.CustomChannels.FirstOrDefault(c => c.Key == key);
            if (custom is not null && o.Name is not null)
            {
                custom.Name = o.Name;
            }
        }

        if (req.Number.HasValue)
        {
            o.Number = req.Number;
        }

        if (req.Weighted.HasValue)
        {
            o.Weighted = req.Weighted.Value;
        }

        if (req.EpisodesInARow.HasValue)
        {
            o.EpisodesInARow = Math.Clamp(req.EpisodesInARow.Value, 1, 6);
        }

        if (req.Order is not null)
        {
            o.Order = string.Equals(req.Order, "inorder", StringComparison.OrdinalIgnoreCase) ? "inorder" : "shuffle";
        }

        if (req.YearFrom.HasValue)
        {
            o.YearFrom = req.YearFrom.Value is > 0 and < 10000 ? req.YearFrom : null;
        }

        if (req.YearTo.HasValue)
        {
            o.YearTo = req.YearTo.Value is > 0 and < 10000 ? req.YearTo : null;
        }

        if (o.YearFrom.HasValue && o.YearTo.HasValue && o.YearFrom > o.YearTo)
        {
            (o.YearFrom, o.YearTo) = (o.YearTo, o.YearFrom);
        }

        if (req.EraAdd is not null)
        {
            var scope = req.EraAdd.Trim().ToLowerInvariant();
            o.EraAdd = scope is "movies" or "shows" or "all" or "musicvideos" ? scope : null;
        }

        if (req.SeasonStart is not null)
        {
            o.SeasonStart = ChannelRules.ParseMonthDay(req.SeasonStart) is int ss ? $"{ss / 100:00}-{ss % 100:00}" : null;
        }

        if (req.SeasonEnd is not null)
        {
            o.SeasonEnd = ChannelRules.ParseMonthDay(req.SeasonEnd) is int se ? $"{se / 100:00}-{se % 100:00}" : null;
        }

        return Changed(key);
    }

    /// <summary>Per-show "episodes in a row" (0 = channel default).</summary>
    [HttpPost("channels/{key}/showblock")]
    public IActionResult SetShowBlock([FromRoute] string key, [FromBody] ShowBlockRequest req)
    {
        var o = Edit(key);
        o.ShowBlocks.RemoveAll(b => b.SeriesId == req.ItemId);
        if (req.Count > 0)
        {
            o.ShowBlocks.Add(new ShowBlock { SeriesId = req.ItemId, Count = Math.Clamp(req.Count, 1, 6) });
        }

        return Changed(key);
    }

    public sealed class CreditsRequest
    {
        public Guid ItemId { get; set; }

        /// <summary>true = always play this show's / movie's credits in full.</summary>
        public bool Keep { get; set; }
    }

    /// <summary>Per show / movie: leave the end credits alone (e.g. credits that roll over the final scene).</summary>
    [HttpPost("credits")]
    public IActionResult SetCredits([FromBody] CreditsRequest req)
    {
        Config.KeepCredits.RemoveAll(g => g == req.ItemId);
        if (req.Keep)
        {
            Config.KeepCredits.Add(req.ItemId);
        }

        Plugin.Instance!.SaveConfiguration();

        // Re-plan every channel that carries it, so the change shows up after what's on now.
        foreach (var ch in ChannelScheduler.GetChannels(_library).Where(c => c.Pool.Any(u => u.Id == req.ItemId)).ToList())
        {
            ChannelScheduler.Replan(_library, ch.Def.Key);
        }

        GuideRefresher.Request();
        return Ok(new { ok = true });
    }

    /// <summary>Every on-air channel: throw away the plan after what's on now and plan again
    /// (after turning on bumpers, changing credit settings, or once Intro Skipper has analyzed the library).</summary>
    /// <summary>Guide refresh state for the settings page (pending / waiting for viewers / running with % / idle + last result).</summary>
    [HttpGet("guide-status")]
    public IActionResult GuideStatus() => Ok(GuideRefresher.Status());

    /// <summary>Update the TV guide now (even if someone is watching).</summary>
    [HttpPost("guide-refresh")]
    public IActionResult GuideRefreshNow()
    {
        GuideRefresher.Request();
        return Ok(new { ok = true });
    }

    /// <summary>Pick the starter lineup again from the library (turns channels on/off and renumbers built-in/genre/network ones).</summary>
    [HttpPost("starter-lineup")]
    public IActionResult StarterLineup()
    {
        var idx = ChannelScheduler.Index(_library);
        if (!ChannelRules.PickStarterLineup(idx, Config))
        {
            return Ok(new { ok = false });
        }

        Config.StarterLineupDone = true;
        Plugin.Instance!.SaveConfiguration();
        ChannelScheduler.Invalidate();
        ChannelScheduler.LineupChanged(_library);
        return RebuildAll();
    }

    [HttpPost("rebuild-all")]
    public IActionResult RebuildAll()
    {
        Credits.ClearCache();
        var keys = ChannelScheduler.GetChannels(_library).Select(c => c.Def.Key).ToList();
        foreach (var key in keys)
        {
            ChannelScheduler.Replan(_library, key);
        }

        GuideRefresher.Request();
        return Ok(new { ok = true, channels = keys.Count });
    }

    /// <summary>Throw away the planned schedule after what's on now and plan it again.</summary>
    [HttpPost("channels/{key}/rebuild")]
    public IActionResult Rebuild([FromRoute] string key) => Changed(key);

    /// <summary>Left-to-right order for the weighted mix.</summary>
    [HttpPost("channels/{key}/ranking")]
    public IActionResult Ranking([FromRoute] string key, [FromBody] RankingRequest req)
    {
        var o = Edit(key);
        o.Ranking = req.ItemIds.Distinct().ToList();
        return Changed(key);
    }

    [HttpPost("channels")]
    public IActionResult Create([FromBody] CreateRequest req)
    {
        var name = string.IsNullOrWhiteSpace(req.Name) ? "My Channel" : req.Name.Trim();
        var key = "custom-" + ChannelRules.Slug(name);
        var n = 2;
        while (Config.CustomChannels.Any(c => c.Key == key))
        {
            key = "custom-" + ChannelRules.Slug(name) + "-" + n++;
        }

        var number = 401 + Config.CustomChannels.Count;
        Config.CustomChannels.Add(new CustomChannel { Key = key, Name = name, Number = number });
        Plugin.Instance!.SaveConfiguration();
        ChannelScheduler.Invalidate();
        return Ok(new { key });
    }

    [HttpDelete("channels/{key}")]
    public IActionResult Delete([FromRoute] string key)
    {
        var removed = Config.CustomChannels.RemoveAll(c => c.Key == key);
        if (removed == 0)
        {
            return BadRequest("Only channels you created can be deleted.");
        }

        Config.Overrides.RemoveAll(o => o.Key == key);
        return Changed(key);
    }

    // ------------------------------------------------------------ helpers

    private ChannelDef? FindDef(LibraryIndex idx, string key)
        => ChannelRules.AllDefs(idx, Config).FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));

    private static ChannelOverride Edit(string key)
    {
        var o = ChannelRules.Override(Config, key);
        if (o is null)
        {
            o = new ChannelOverride { Key = key };
            Config.Overrides.Add(o);
        }

        return o;
    }

    private IActionResult Changed(string key)
    {
        Plugin.Instance!.SaveConfiguration();
        ChannelScheduler.Replan(_library, key);
        ChannelScheduler.LineupChanged(_library); // remember the lineup; this refresh covers it
        GuideRefresher.Request();
        return Ok(new { ok = true });
    }

    private sealed record ItemView(string id, string name, string type, int? year, string reason, string? image);

    private ItemView? Describe(LibraryIndex idx, Guid id, string reason)
    {
        if (idx.Collections.TryGetValue(id, out var col))
        {
            return new ItemView(id.ToString("N"), col.Name, "Collection", null, reason, $"Items/{id:N}/Images/Primary?maxHeight=300");
        }

        BaseItem? item = idx.SeriesById.TryGetValue(id, out var show) ? show : null;
        item ??= idx.Playable.TryGetValue(id, out var p) ? p : _library.GetItemById(id);
        if (item is null)
        {
            return null;
        }

        var type = item switch
        {
            Series => "Show",
            Episode => "Episode",
            MusicVideo => "Music video",
            MediaBrowser.Controller.Entities.Movies.BoxSet => "Collection",
            _ => "Movie"
        };

        return new ItemView(item.Id.ToString("N"), Title(item), type, item.ProductionYear, reason, $"Items/{item.Id:N}/Images/Primary?maxHeight=300");
    }

    private static string Title(BaseItem item)
        => item is Episode ep ? $"{ep.SeriesName} — {ep.Name}" : ChannelRules.DisplayName(item);
}
