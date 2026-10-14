using System.Globalization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.QuasiTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>A schedulable unit on a channel: a whole show (episodes drawn from a deck) or a single movie/episode.</summary>
public sealed record Unit(Guid Id, bool IsSeries, string Reason);

public enum ChannelKind
{
    Core,
    Genre,
    Network,
    Custom
}

/// <summary>A channel definition: key, display name, number and the rule that picks its content.</summary>
public sealed record ChannelDef(string Key, string Name, int Number, Func<LibraryIndex, IEnumerable<Unit>> Rule, ChannelKind Kind = ChannelKind.Core);

/// <summary>The core channel lineup and the metadata rules behind each one (see Spec §3).</summary>
public static class ChannelRules
{
    private static readonly string[] KidsTvRatings = { "TV-Y", "TV-Y7", "TV-Y7-FV", "TV-G", "TV-PG" };
    private static readonly string[] KidsMovieRatings = { "G", "PG", "TV-G", "TV-Y", "TV-PG" };
    private static readonly string[] AdultNetworks = { "Adult Swim", "Comedy Central", "FX", "FXX", "FX Networks" };
    private static readonly string[] HalloweenWords =
        { "halloween", "treehouse of horror", "trick or treat", "trick-or-treat", "haunted", "spooky", "all hallows", "jack-o'-lantern", "pumpkin" };

    /// <summary>Names the built-ins had before 0.9.2 — kept for existing installs so nobody's guide gets renamed on update.</summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyNames = new Dictionary<string, string>
    {
        ["adultswim"] = "Adult Swim", ["nick"] = "Nickelodeon", ["fx"] = "FX", ["tcm"] = "Turner Classics",
        ["lifetime"] = "Lifetime", ["hbo"] = "HBO", ["shudder"] = "Shudder",
    };

    public static readonly IReadOnlyList<ChannelDef> Core = new[]
    {
        new ChannelDef("adultswim", "After Hours", 101, idx => Shows(idx, s =>
            Has(s.Genres, "Animation") && (RatingIn(s, "TV-14", "TV-MA") || AnyStudio(s, AdultNetworks)), "Adult animation")),

        new ChannelDef("nick", "Saturday Morning", 102, idx => Shows(idx, s =>
            (Has(s.Genres, "Animation") || Has(s.Genres, "Kids")) && RatingIn(s, KidsTvRatings), "Kids show")),

        new ChannelDef("fx", "Action Pack", 103, idx => Movies(idx, m =>
            HasAny(m.Genres, "Action", "Thriller", "Crime", "War") && RatingIn(m, "PG-13", "R") && (m.ProductionYear ?? 0) >= 1985, "Action/thriller")),

        new ChannelDef("tcm", "Classic Cinema", 104, idx => Movies(idx, m =>
            m.ProductionYear is > 0 and < 1985, "Pre-1985")),

        new ChannelDef("lifetime", "Tearjerkers", 105, idx => Movies(idx, m =>
            HasAny(m.Genres, "Romance", "TV Movie")
            && !HasAny(m.Genres, "Action", "Horror", "Science Fiction", "Fantasy", "Animation", "War", "Family", "History"), "Romance/TV movie")),

        new ChannelDef("hbo", "Prestige TV", 106, idx =>
            Shows(idx, s => s.Studios.Any(x => x.Contains("HBO", StringComparison.OrdinalIgnoreCase)) || Acclaimed(s), "Acclaimed show")
            .Concat(Movies(idx, Acclaimed, "Acclaimed"))),

        new ChannelDef("shudder", "Fright Night", 107, idx =>
            Movies(idx, m => Has(m.Genres, "Horror"), "Horror")
            .Concat(HalloweenSeason() ? HalloweenEpisodes(idx) : Enumerable.Empty<Unit>())),

        new ChannelDef("kids", "Kids Classics", 108, idx => Movies(idx, m =>
            HasAny(m.Genres, "Family", "Animation") && RatingIn(m, KidsMovieRatings), "Kids movie")),
    };

    /// <summary>Every channel that could exist: core + auto genre/network channels + your custom ones, with editor names/numbers applied.</summary>
    public static List<ChannelDef> AllDefs(LibraryIndex idx, PluginConfiguration cfg)
    {
        var defs = new List<ChannelDef>(Core);

        // Genre movie channels (alphabetical so numbers stay put as the library grows).
        var genres = idx.Movies
            .SelectMany(m => m.Genres ?? Array.Empty<string>())
            .Where(UsableName)
            .Select(g => g.Trim())
            .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= Math.Max(1, cfg.MinGenreTitles))
            .Select(g => g.Key)
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < genres.Count; i++)
        {
            var genre = genres[i];
            defs.Add(new ChannelDef("genre-" + Slug(genre), genre + " Movies", 201 + i, x => Movies(x, m => Has(m.Genres, genre), genre), ChannelKind.Genre));
        }

        // Network channels.
        var networks = idx.Series
            .SelectMany(s => s.Studios ?? Array.Empty<string>())
            .Where(UsableName)
            .Select(n => n.Trim())
            .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() >= Math.Max(1, cfg.MinNetworkShows))
            .Select(g => g.Key)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < networks.Count; i++)
        {
            var network = networks[i];
            defs.Add(new ChannelDef("net-" + Slug(network), network, 301 + i, x => Shows(x, s => Has(s.Studios, network), network), ChannelKind.Network));
        }

        // Your channels: content comes only from what you add.
        defs.AddRange(cfg.CustomChannels.Select(c => new ChannelDef(c.Key, c.Name, c.Number, _ => Enumerable.Empty<Unit>(), ChannelKind.Custom)));

        // Editor renames / renumbers.
        return defs.Select(d =>
        {
            var o = Override(cfg, d.Key);
            return o is null ? d : d with { Name = string.IsNullOrWhiteSpace(o.Name) ? d.Name : o.Name!, Number = o.Number ?? d.Number };
        }).ToList();
    }

    public const int StarterLineupSize = 10;

    /// <summary>
    /// Fresh installs: turn on the channels this library can fill best and number them 101+.
    /// Score = hours of content (built-ins get a small boost, they're mixed on purpose); channels that are mostly
    /// the same titles as one already picked are skipped. Everything else is switched off (still there to turn on).
    /// Returns false if the library looks too empty to judge yet (scan not done) — try again later.
    /// </summary>
    public static bool PickStarterLineup(LibraryIndex idx, PluginConfiguration cfg)
    {
        if (idx.Movies.Count + idx.Series.Count < 10)
        {
            return false;
        }

        double Hours(IEnumerable<Unit> pool) => pool.Sum(u => u.IsSeries
            ? (idx.EpisodesBySeries.TryGetValue(u.Id, out var eps) ? eps.Sum(e => e.RunTimeTicks ?? 0) : 0)
            : (idx.Playable.TryGetValue(u.Id, out var it) ? it.RunTimeTicks ?? 0 : 0)) / (double)TimeSpan.TicksPerHour;

        var scored = AllDefs(idx, cfg)
            .Where(d => d.Kind is ChannelKind.Core or ChannelKind.Genre or ChannelKind.Network)
            .Select(d =>
            {
                var pool = Pool(d, idx, cfg);
                var hours = Hours(pool);
                return (Def: d, Ids: pool.Select(u => u.Id).ToHashSet(), Score: hours * (d.Kind == ChannelKind.Core ? 1.25 : 1.0));
            })
            .Where(c => c.Ids.Count >= 4 && c.Score >= 12)
            .OrderByDescending(c => c.Score)
            .ToList();

        var picked = new List<(ChannelDef Def, HashSet<Guid> Ids, double Score)>();
        foreach (var c in scored)
        {
            if (picked.Count >= StarterLineupSize)
            {
                break;
            }

            var dupe = picked.Any(p => c.Ids.Count(p.Ids.Contains) > 0.6 * c.Ids.Count);
            if (!dupe)
            {
                picked.Add(c);
            }
        }

        ChannelOverride Edit(string key)
        {
            var o = Override(cfg, key);
            if (o is null)
            {
                o = new ChannelOverride { Key = key };
                cfg.Overrides.Add(o);
            }

            return o;
        }

        var pickedKeys = picked.Select(p => p.Def.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < picked.Count; i++)
        {
            var o = Edit(picked[i].Def.Key);
            o.Enabled = true;
            o.Number = 101 + i;
        }

        var spare = 101 + picked.Count;
        foreach (var d in AllDefs(idx, cfg).Where(d => (d.Kind is ChannelKind.Core or ChannelKind.Genre or ChannelKind.Network) && !pickedKeys.Contains(d.Key)))
        {
            var o = Edit(d.Key);
            o.Enabled = false;
            if (d.Kind == ChannelKind.Core)
            {
                o.Number = spare++; // move unpicked built-ins out of the way of the starter numbers
            }
        }

        return true;
    }

    public static bool IsEnabled(ChannelDef def, PluginConfiguration cfg)
        => Override(cfg, def.Key)?.Enabled ?? def.Kind is ChannelKind.Core or ChannelKind.Custom;

    /// <summary>Switched on and in season: actually on the guide right now.</summary>
    public static bool OnAir(ChannelDef def, PluginConfiguration cfg)
        => IsEnabled(def, cfg) && InSeason(Override(cfg, def.Key), DateTime.Now);

    /// <summary>True when there's no season set, or <paramref name="when"/> falls inside it (wraps past New Year, e.g. 12-01 to 01-05).</summary>
    public static bool InSeason(ChannelOverride? o, DateTime when)
    {
        var start = ParseMonthDay(o?.SeasonStart);
        var end = ParseMonthDay(o?.SeasonEnd);
        if (start is null || end is null)
        {
            return true;
        }

        var today = (when.Month * 100) + when.Day;
        return start <= end ? today >= start && today <= end : today >= start || today <= end;
    }

    /// <summary>"10-31" or "10/31" -> 1031; anything invalid -> null.</summary>
    public static int? ParseMonthDay(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return null;
        }

        var parts = s.Trim().Split('-', '/');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var m)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d)
            || m < 1 || m > 12 || d < 1 || d > DateTime.DaysInMonth(2024, m))
        {
            return null;
        }

        return (m * 100) + d;
    }

    /// <summary>First and last year a title was on: a movie's release year, or a show's whole run.</summary>
    private static (int First, int Last)? YearSpan(BaseItem item, LibraryIndex idx)
    {
        if (item is Series s)
        {
            var years = idx.EpisodesBySeries.TryGetValue(s.Id, out var eps)
                ? eps.Select(e => e.PremiereDate?.Year ?? e.ProductionYear ?? 0).Where(y => y > 0).ToList()
                : new List<int>();
            if (s.ProductionYear is int py && py > 0)
            {
                years.Add(py);
            }

            if (s.EndDate is DateTime ed)
            {
                years.Add(ed.Year);
            }

            return years.Count == 0 ? null : (years.Min(), years.Max());
        }

        var y = item.ProductionYear ?? item.PremiereDate?.Year ?? 0;
        return y > 0 ? (y, y) : null;
    }

    private static bool InEra((int First, int Last)? span, int from, int to)
        => span.HasValue && span.Value.First <= to && span.Value.Last >= from;

    public static bool InOrder(ChannelDef def, PluginConfiguration cfg)
        => string.Equals(Override(cfg, def.Key)?.Order, "inorder", StringComparison.OrdinalIgnoreCase);

    public static ChannelOverride? Override(PluginConfiguration cfg, string key)
        => cfg.Overrides.FirstOrDefault(o => string.Equals(o.Key, key, StringComparison.OrdinalIgnoreCase));

    /// <summary>Build a channel's pool: rule matches + your additions + "cable:&lt;key&gt;" tags − removals − "cable:exclude" (old "quasi:" tags also work).</summary>
    public static List<Unit> Pool(ChannelDef def, LibraryIndex idx, PluginConfiguration cfg)
    {
        var addTag = "cable:" + def.Key;
        var oldTag = "quasi:" + def.Key;
        var o = Override(cfg, def.Key);
        var removed = o?.Removed.ToHashSet() ?? new HashSet<Guid>();
        var units = new Dictionary<Guid, Unit>();

        foreach (var u in def.Rule(idx))
        {
            units.TryAdd(u.Id, u);
        }

        foreach (var s in idx.Series.Where(s => Has(s.Tags, addTag) || Has(s.Tags, oldTag)))
        {
            units[s.Id] = new Unit(s.Id, true, "Tagged " + addTag);
        }

        foreach (var m in idx.Movies.Where(m => Has(m.Tags, addTag) || Has(m.Tags, oldTag)))
        {
            units[m.Id] = new Unit(m.Id, false, "Tagged " + addTag);
        }

        foreach (var e in idx.EpisodesBySeries.Values.SelectMany(x => x).Where(e => Has(e.Tags, addTag) || Has(e.Tags, oldTag)))
        {
            units[e.Id] = new Unit(e.Id, false, "Tagged " + addTag);
        }

        // Whole genres and keyword tags.
        void IncludeBy(IEnumerable<string> entries, Func<BaseItem, string[]?> field, string label)
        {
            foreach (var entry in entries)
            {
                var (scope, name) = SplitGenre(entry);
                if (scope != "shows")
                {
                    foreach (var m in idx.Movies.Where(m => Has(field(m), name)))
                    {
                        units.TryAdd(m.Id, new Unit(m.Id, false, label + name));
                    }
                }

                if (scope != "movies")
                {
                    foreach (var sh in idx.Series.Where(x => Has(field(x), name)))
                    {
                        units.TryAdd(sh.Id, new Unit(sh.Id, true, label + name));
                    }
                }
            }
        }

        IncludeBy(o?.IncludeGenres ?? new List<string>(), i => i.Genres, "Genre: ");
        IncludeBy(o?.IncludeTags ?? new List<string>(), i => i.Tags, "Tag: ");
        IncludeBy(o?.IncludeStudios ?? new List<string>(), i => i.Studios, "Studio: ");

        var excludeGenres = (o?.ExcludeGenres ?? new List<string>()).ToArray();
        var excludeTags = (o?.ExcludeTags ?? new List<string>()).ToArray();
        var excludeStudios = (o?.ExcludeStudios ?? new List<string>()).ToArray();
        if (excludeGenres.Length > 0 || excludeTags.Length > 0 || excludeStudios.Length > 0)
        {
            foreach (var u in units.Values.ToList())
            {
                BaseItem? item = u.IsSeries
                    ? (idx.SeriesById.TryGetValue(u.Id, out var sh) ? sh : null)
                    : (idx.Playable.TryGetValue(u.Id, out var pl) ? pl : null);
                if (item is not null && (HasAny(item.Genres, excludeGenres) || HasAny(item.Tags, excludeTags) || HasAny(item.Studios, excludeStudios)))
                {
                    units.Remove(u.Id);
                }
            }
        }

        // Era: bring in everything from the years, then keep only titles from the years.
        if (o is not null && (o.YearFrom.HasValue || o.YearTo.HasValue))
        {
            var from = o.YearFrom ?? 1;
            var to = o.YearTo ?? 9999;
            var label = from == to ? $"Era: {from}" : $"Era: {(o.YearFrom?.ToString(CultureInfo.InvariantCulture) ?? "…")}–{(o.YearTo?.ToString(CultureInfo.InvariantCulture) ?? "…")}";
            var addScope = o.EraAdd?.ToLowerInvariant();
            if (addScope is "movies" or "all")
            {
                foreach (var m in idx.Movies.Where(m => InEra(YearSpan(m, idx), from, to)))
                {
                    units.TryAdd(m.Id, new Unit(m.Id, false, label));
                }
            }

            if (addScope is "shows" or "all")
            {
                foreach (var sh in idx.Series.Where(s => InEra(YearSpan(s, idx), from, to)))
                {
                    units.TryAdd(sh.Id, new Unit(sh.Id, true, label));
                }
            }

            foreach (var u in units.Values.ToList())
            {
                BaseItem? item = u.IsSeries
                    ? (idx.SeriesById.TryGetValue(u.Id, out var sh) ? sh : null)
                    : (idx.Playable.TryGetValue(u.Id, out var pl) ? pl : null);
                if (item is not null && !InEra(YearSpan(item, idx), from, to))
                {
                    units.Remove(u.Id);
                }
            }
        }

        // One-by-one additions and collections (these win over genre exclusions).
        foreach (var id in o?.Added ?? new List<Guid>())
        {
            if (idx.Collections.TryGetValue(id, out var col))
            {
                foreach (var m in col.Members)
                {
                    units.TryAdd(m, new Unit(m, idx.SeriesById.ContainsKey(m), "From " + col.Name));
                }
            }
            else if (idx.SeriesById.ContainsKey(id))
            {
                units[id] = new Unit(id, true, "Added by you");
            }
            else if (idx.Playable.ContainsKey(id))
            {
                units[id] = new Unit(id, false, "Added by you");
            }
        }

        return units.Values.Where(u => !removed.Contains(u.Id) && !Excluded(u, idx)).ToList();
    }

    /// <summary>"movies:Comedy" -> ("movies", "Comedy"); bare "Comedy" -> ("all", "Comedy").</summary>
    public static (string Scope, string Genre) SplitGenre(string entry)
    {
        var i = entry.IndexOf(':', StringComparison.Ordinal);
        if (i > 0)
        {
            var scope = entry[..i].ToLowerInvariant();
            if (scope is "movies" or "shows" or "all")
            {
                return (scope, entry[(i + 1)..]);
            }
        }

        return ("all", entry);
    }

    /// <summary>
    /// Weighted mix: rank r (1 = most) gets a share ∝ 1 / r^0.72. With 6 titles that's about 33/20/15/12/10/9 %.
    /// Titles missing from the ranking (newly added) go to the end in name order.
    /// </summary>
    public const double WeightCurve = 0.72;

    public static List<Guid> EffectiveRanking(IEnumerable<Guid> poolIds, IReadOnlyList<Guid> ranking, Func<Guid, string> name)
    {
        var ids = poolIds.ToHashSet();
        var ordered = ranking.Where(ids.Contains).Distinct().ToList();
        ordered.AddRange(ids.Except(ordered).OrderBy(name, StringComparer.OrdinalIgnoreCase));
        return ordered;
    }

    /// <summary>
    /// Draw weights for a ranked list. The scheduler never repeats a title back-to-back on weighted channels,
    /// which drags the top title's share down — so for up to 40 titles we pre-compensate (iterative scaling on the
    /// no-repeat Markov chain) until the long-run shares match the target curve.
    /// </summary>
    public static Dictionary<Guid, double> RankWeights(IReadOnlyList<Guid> ranked)
    {
        var n = ranked.Count;
        var target = new double[n];
        for (var i = 0; i < n; i++)
        {
            target[i] = 1.0 / Math.Pow(i + 1, WeightCurve);
        }

        var sum = target.Sum();
        for (var i = 0; i < n; i++)
        {
            target[i] /= sum;
        }

        var w = (double[])target.Clone();
        if (n >= 3 && n <= 40)
        {
            for (var iter = 0; iter < 80; iter++)
            {
                var total = w.Sum();
                var pi = Enumerable.Repeat(1.0 / n, n).ToArray();
                for (var step = 0; step < 200; step++)
                {
                    var next = new double[n];
                    for (var a = 0; a < n; a++)
                    {
                        var denom = total - w[a];
                        if (denom <= 0)
                        {
                            continue;
                        }

                        for (var b = 0; b < n; b++)
                        {
                            if (a != b)
                            {
                                next[b] += pi[a] * w[b] / denom;
                            }
                        }
                    }

                    pi = next;
                }

                for (var i = 0; i < n; i++)
                {
                    if (pi[i] > 0)
                    {
                        w[i] *= target[i] / pi[i];
                    }
                }

                var s2 = w.Sum();
                for (var i = 0; i < n; i++)
                {
                    w[i] /= s2;
                }
            }
        }

        var result = new Dictionary<Guid, double>();
        for (var i = 0; i < n; i++)
        {
            result[ranked[i]] = w[i];
        }

        return result;
    }

    public static string Slug(string s)
    {
        var slug = Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (slug.Length > 0)
        {
            return slug;
        }

        // Names with no a-z/0-9 (e.g. non-Latin studio names) still need a unique, stable key.
        var hash = System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(s.Trim().ToLowerInvariant()));
        return "u" + Convert.ToHexString(hash, 0, 5).ToLowerInvariant();
    }

    /// <summary>Real names only — metadata sometimes carries blank or punctuation-only genres/studios.</summary>
    private static bool UsableName(string? s) => !string.IsNullOrWhiteSpace(s) && s.Any(char.IsLetterOrDigit);

    private static bool Excluded(Unit u, LibraryIndex idx)
    {
        if (idx.Playable.TryGetValue(u.Id, out var item) && (Has(item.Tags, "cable:exclude") || Has(item.Tags, "quasi:exclude")))
        {
            return true;
        }

        return idx.SeriesById.TryGetValue(u.Id, out var series) && (Has(series.Tags, "cable:exclude") || Has(series.Tags, "quasi:exclude"));
    }

    private static IEnumerable<Unit> Shows(LibraryIndex idx, Func<Series, bool> rule, string reason)
        => idx.Series.Where(rule).Select(s => new Unit(s.Id, true, reason));

    private static IEnumerable<Unit> Movies(LibraryIndex idx, Func<BaseItem, bool> rule, string reason)
        => idx.Movies.Where(rule).Select(m => new Unit(m.Id, false, reason));

    private static IEnumerable<Unit> HalloweenEpisodes(LibraryIndex idx)
        => idx.EpisodesBySeries.Values.SelectMany(x => x)
            .Where(e => HalloweenWords.Any(w =>
                (e.Name ?? string.Empty).Contains(w, StringComparison.OrdinalIgnoreCase)
                || (e.Overview ?? string.Empty).Contains(w, StringComparison.OrdinalIgnoreCase)))
            .Select(e => new Unit(e.Id, false, "Halloween special"));

    private static bool HalloweenSeason() => DateTime.Now.Month == 10;

    private static bool Acclaimed(BaseItem i) => (i.CommunityRating ?? 0) >= 7.5f && (i.CriticRating ?? 0) >= 90f;

    private static bool Has(string[]? values, string v)
        => values is not null && values.Any(x => string.Equals(x?.Trim(), v.Trim(), StringComparison.OrdinalIgnoreCase));

    private static bool HasAny(string[]? values, params string[] vs) => vs.Any(v => Has(values, v));

    private static bool AnyStudio(BaseItem i, string[] names) => HasAny(i.Studios, names);

    private static bool RatingIn(BaseItem i, params string[] ratings)
        => ratings.Any(r => string.Equals(r, i.OfficialRating, StringComparison.OrdinalIgnoreCase));
}
