using System.Text.Json;
using Jellyfin.Plugin.QuasiTV.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>One program airing on a channel (real wall-clock times).
/// It opens with an optional "Up Next" bumper (<paramref name="Bumper"/>), then plays <paramref name="Play"/> of the item
/// (less than its runtime when the end credits are cut short: <paramref name="Trimmed"/>).</summary>
public sealed record Slot(BaseItem Item, DateTime StartUtc, DateTime EndUtc, TimeSpan Bumper = default, TimeSpan Play = default, bool Trimmed = false)
{
    /// <summary>When the item itself starts (after the bumper).</summary>
    public DateTime ItemStartUtc => StartUtc + Bumper;
}

/// <summary>A live channel: its definition + current content pool.</summary>
public sealed class QuasiChannel
{
    public QuasiChannel(ChannelDef def, IReadOnlyList<Unit> pool, bool inOrder = false, int inARow = 1, IReadOnlyDictionary<Guid, int>? showInARow = null, IReadOnlyDictionary<Guid, double>? weights = null)
    {
        Weights = weights;
        Def = def;
        Pool = pool;
        InOrder = inOrder;
        InARow = Math.Clamp(inARow, 1, 6);
        ShowInARow = showInARow ?? new Dictionary<Guid, int>();
    }

    /// <summary>Episodes played back-to-back when a show comes up (channel default).</summary>
    public int InARow { get; }

    /// <summary>Per-show overrides of <see cref="InARow"/>.</summary>
    public IReadOnlyDictionary<Guid, int> ShowInARow { get; }

    /// <summary>Weighted mix (null = every title equally likely).</summary>
    public IReadOnlyDictionary<Guid, double>? Weights { get; }

    public int BlockFor(Guid seriesId) => ShowInARow.TryGetValue(seriesId, out var n) && n > 0 ? Math.Clamp(n, 1, 6) : InARow;

    public ChannelDef Def { get; }

    /// <summary>Play in release order (looping) instead of shuffling.</summary>
    public bool InOrder { get; }

    public IReadOnlyList<Unit> Pool { get; }

    public string Id => "quasi-" + Def.Key;

    public string Name => Def.Name;

    public int Number => Def.Number;
}

/// <summary>
/// Builds channels from the library and keeps a rolling, persisted schedule per channel
/// (~3 days ahead) in "schedule time". Real time = schedule time + the channel's shift
/// (shifts come from Record-jumps / restarts). Nothing plays until someone tunes in.
/// </summary>
public static class ChannelScheduler
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Ahead = TimeSpan.FromDays(3);
    private static readonly TimeSpan ExtendWhenBelow = TimeSpan.FromDays(2);
    private static readonly TimeSpan KeepBehind = TimeSpan.FromHours(12);
    private static readonly TimeSpan MovieNoRepeat = TimeSpan.FromDays(2);
    private const int ShowGap = 2;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly Dictionary<string, ChannelState> States = new(StringComparer.OrdinalIgnoreCase);
    private static List<QuasiChannel>? _channels;
    private static LibraryIndex? _index;
    private static DateTime _builtAt;

    private static PluginConfiguration Config => Plugin.Instance!.Configuration;

    // ------------------------------------------------------------ channels

    public static IReadOnlyList<QuasiChannel> GetChannels(ILibraryManager library)
    {
        lock (Gate)
        {
            if (_channels is null || _index is null || DateTime.UtcNow - _builtAt > CacheFor)
            {
                _index = LibraryIndex.Build(library);
                if (!Config.StarterLineupDone && ChannelRules.PickStarterLineup(_index, Config))
                {
                    Config.StarterLineupDone = true;
                    Plugin.Instance!.SaveConfiguration();
                }

                _channels = ChannelRules.AllDefs(_index, Config)
                    .Where(def => ChannelRules.OnAir(def, Config))
                    .Select(def =>
                    {
                        var o = ChannelRules.Override(Config, def.Key);
                        var perShow = (o?.ShowBlocks ?? new List<ShowBlock>()).Where(b => b.Count > 0).GroupBy(b => b.SeriesId).ToDictionary(g => g.Key, g => g.Last().Count);
                        var pool = ChannelRules.Pool(def, _index, Config);
                        Dictionary<Guid, double>? weights = null;
                        if (o?.Weighted == true)
                        {
                            var idx = _index;
                            var ranked = ChannelRules.EffectiveRanking(pool.Select(u => u.Id), o.Ranking, id =>
                                idx.SeriesById.TryGetValue(id, out var sr) ? sr.SortName ?? sr.Name ?? string.Empty
                                : idx.Playable.TryGetValue(id, out var it) ? it.SortName ?? it.Name ?? string.Empty : string.Empty);
                            weights = ChannelRules.RankWeights(ranked);
                        }

                        return new QuasiChannel(def, pool, ChannelRules.InOrder(def, Config), o?.EpisodesInARow ?? 1, perShow, weights);
                    })
                    .Where(c => c.Pool.Count > 0)
                    .OrderBy(c => c.Number)
                    .ToList();
                _builtAt = DateTime.UtcNow;
            }

            return _channels;
        }
    }

    /// <summary>Force a rebuild on next access (after library or editor changes).</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _channels = null;
        }
    }

    /// <summary>Current library snapshot (built with the channels).</summary>
    public static LibraryIndex Index(ILibraryManager library)
    {
        lock (Gate)
        {
            GetChannels(library);
            return _index!;
        }
    }

    /// <summary>After an editor change: rebuild channels and re-plan everything after the program that's on now.</summary>
    public static void Replan(ILibraryManager library, string channelKey)
    {
        lock (Gate)
        {
            Invalidate();
            var channel = GetChannels(library).FirstOrDefault(c => string.Equals(c.Def.Key, channelKey, StringComparison.OrdinalIgnoreCase));
            var state = Load("quasi-" + channelKey);
            var nowSched = DateTime.UtcNow.AddTicks(-GetShift("quasi-" + channelKey));
            TrimAfter(state, nowSched);
            if (channel is not null)
            {
                state.Signature = Signature(channel); // this edit's refresh is handled by the caller
            }

            if (channel is not null && state.Entries.Count > 0)
            {
                Ensure(channel, nowSched, nowSched.AddHours(1));
            }
            else
            {
                Save("quasi-" + channelKey, state);
            }
        }
    }

    public static QuasiChannel? Find(ILibraryManager library, string id)
        => GetChannels(library).FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));

    public static QuasiChannel? FindByName(ILibraryManager library, string? name)
        => name is null ? null : GetChannels(library).FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase));

    // ------------------------------------------------------------ schedule queries

    /// <summary>Programs overlapping [fromUtc, untilUtc) in real time.</summary>
    public static IReadOnlyList<Slot> Slots(QuasiChannel channel, DateTime fromUtc, DateTime untilUtc)
    {
        lock (Gate)
        {
            var shift = GetShift(channel.Id);
            var from = fromUtc.AddTicks(-shift);
            var until = untilUtc.AddTicks(-shift);

            var state = Ensure(channel, from, until);
            var result = new List<Slot>();
            foreach (var e in state.Entries)
            {
                var end = e.Start.AddTicks(e.Ticks);
                if (end <= from || e.Start >= until)
                {
                    continue;
                }

                if (_index!.Playable.TryGetValue(e.ItemId, out var item))
                {
                    var play = e.PlayTicks > 0 ? e.PlayTicks : e.Ticks - e.BumperTicks;
                    result.Add(new Slot(item, e.Start.AddTicks(shift), end.AddTicks(shift), TimeSpan.FromTicks(e.BumperTicks), TimeSpan.FromTicks(play), e.Trimmed));
                }
            }

            return result;
        }
    }

    public static Slot? Current(QuasiChannel channel, DateTime nowUtc)
        => Slots(channel, nowUtc, nowUtc.AddTicks(1)).FirstOrDefault();

    // ------------------------------------------------------------ shifts (restart / skip / jump)

    public static void Restart(QuasiChannel channel)
    {
        var slot = Current(channel, DateTime.UtcNow);
        if (slot is not null)
        {
            MoveToNow(channel, slot.StartUtc);
        }
    }

    public static void Skip(QuasiChannel channel)
    {
        var slot = Current(channel, DateTime.UtcNow);
        if (slot is not null)
        {
            MoveToNow(channel, slot.EndUtc);
        }
    }

    /// <summary>Shift the channel so whatever starts at <paramref name="realStartUtc"/> (as currently scheduled) starts now.</summary>
    public static void MoveToNow(QuasiChannel channel, DateTime realStartUtc)
        => AddShift(channel.Id, (DateTime.UtcNow - realStartUtc).Ticks);

    /// <summary>Shift the channel so the program at schedule-time <paramref name="scheduleStart"/> starts now.</summary>
    public static void MoveScheduleTimeToNow(QuasiChannel channel, DateTime scheduleStart)
        => MoveToNow(channel, scheduleStart.AddTicks(GetShift(channel.Id)));

    public static DateTime ToScheduleTime(QuasiChannel channel, DateTime realUtc)
        => realUtc.AddTicks(-GetShift(channel.Id));

    private static long GetShift(string channelId)
    {
        lock (Gate)
        {
            return Config.Shifts.FirstOrDefault(s => s.ChannelId == channelId)?.ShiftTicks ?? 0;
        }
    }

    private static void AddShift(string channelId, long ticks)
    {
        lock (Gate)
        {
            var entry = Config.Shifts.FirstOrDefault(s => s.ChannelId == channelId);
            if (entry is null)
            {
                entry = new ChannelShift { ChannelId = channelId };
                Config.Shifts.Add(entry);
            }

            entry.ShiftTicks += ticks;
            Plugin.Instance!.SaveConfiguration();
        }
    }

    // ------------------------------------------------------------ schedule generation (call under Gate)

    /// <summary>Throw away everything planned to start after <paramref name="schedTime"/> and undo its bookkeeping.</summary>
    private static void TrimAfter(ChannelState state, DateTime schedTime)
    {
        var cut = state.Entries.FindIndex(e => e.Start > schedTime);
        if (cut >= 0)
        {
            // Episodes we'd drawn for the discarded plan go back into their show's deck.
            foreach (var e in state.Entries.Skip(cut).Where(e => e.ItemId != e.UnitId))
            {
                if (state.Decks.TryGetValue(e.UnitId.ToString("N"), out var deck) && !deck.Contains(e.ItemId))
                {
                    deck.Insert(Random.Shared.Next(deck.Count + 1), e.ItemId);
                }
            }

            state.Entries.RemoveRange(cut, state.Entries.Count - cut);
        }

        // Forget picks from the plan we just threw away, so the new plan isn't skewed by it.
        state.Recent = state.Entries.Select(e => e.UnitId).TakeLast(20).ToList();
        foreach (var k in state.LastAired.Where(kv => kv.Value > schedTime).Select(kv => kv.Key).ToList())
        {
            state.LastAired.Remove(k);
        }

        foreach (var e in state.Entries)
        {
            state.LastAired[e.UnitId.ToString("N")] = e.Start;
        }

        state.LastOrderedItem = state.Entries.Count > 0 ? state.Entries[^1].ItemId : null;
    }

    /// <summary>Fingerprint of a channel's content (titles + episode counts) to notice library changes.</summary>
    private static string Signature(QuasiChannel channel)
    {
        var parts = channel.Pool
            .Select(u => u.IsSeries && _index!.EpisodesBySeries.TryGetValue(u.Id, out var eps) ? $"{u.Id:N}:{eps.Count}" : u.Id.ToString("N"))
            .OrderBy(x => x, StringComparer.Ordinal);
        var joined = string.Join(",", parts) + $"|{channel.InOrder}|{channel.InARow}|" + (channel.Weights is null ? "-" : string.Join(",", channel.Weights.OrderByDescending(k => k.Value).Select(k => k.Key.ToString("N"))))
            + $"|b{Config.ShowBumpers}:{Config.BumperSeconds}|c{Config.TrimCredits}:{Config.CreditsKeepSecondsTv}:{Config.CreditsKeepSecondsMovie}";
        return Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(joined)));
    }

    /// <summary>Hours of the guide kept as-is when the library changes (so what's on soon doesn't reshuffle).</summary>
    private static readonly TimeSpan KeepStableAhead = TimeSpan.FromHours(6);

    /// <summary>True (once) if channels were added/removed/renamed/renumbered since last check.</summary>
    public static bool LineupChanged(ILibraryManager library)
    {
        lock (Gate)
        {
            var sig = string.Join("|", GetChannels(library).Select(c => $"{c.Id}:{c.Name}:{c.Number}"));
            var path = Path.Combine(BaseDir(), "lineup.txt");
            try
            {
                var old = File.Exists(path) ? File.ReadAllText(path) : null;
                if (old == sig)
                {
                    return false;
                }

                Directory.CreateDirectory(BaseDir());
                File.WriteAllText(path, sig);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }

    private static ChannelState Ensure(QuasiChannel channel, DateTime fromSched, DateTime untilSched)
    {
        var state = Load(channel.Id);
        var changed = false;
        var nowSched = DateTime.UtcNow.AddTicks(-GetShift(channel.Id));

        // Library (or channel content) changed since this plan was made: re-plan beyond the next few hours.
        var sig = Signature(channel);
        if (state.Signature != sig)
        {
            if (state.Signature is not null && state.Entries.Count > 0)
            {
                TrimAfter(state, nowSched + KeepStableAhead);
                GuideRefresher.Request(waitUntilIdle: true);
            }

            state.Signature = sig;
            changed = true;
        }

        // Drop the distant past.
        var cutoff = (fromSched < nowSched ? fromSched : nowSched) - KeepBehind;
        if (state.Entries.RemoveAll(e => e.Start.AddTicks(e.Ticks) < cutoff) > 0)
        {
            changed = true;
        }

        // Nothing covers "now" (first run, or server was off for days): start fresh here.
        if (state.Entries.Count == 0 || state.Entries[^1].Start.AddTicks(state.Entries[^1].Ticks) < fromSched)
        {
            state.Entries.Clear();
            var start = fromSched < nowSched ? fromSched : nowSched;
            state.Entries.AddRange(Next(channel, state, new DateTime(start.Ticks - (start.Ticks % TimeSpan.TicksPerMinute), DateTimeKind.Utc)));
            changed = true;
        }

        // Keep ~3 days ahead of both "now" and whatever was asked for.
        var need = untilSched > nowSched + Ahead ? untilSched : nowSched + Ahead;
        var end = state.Entries[^1].Start.AddTicks(state.Entries[^1].Ticks);
        if (end < need - (Ahead - ExtendWhenBelow) || end < untilSched)
        {
            while (end < need)
            {
                foreach (var e in Next(channel, state, end))
                {
                    state.Entries.Add(e);
                    end = e.Start.AddTicks(e.Ticks);
                }
            }

            changed = true;
        }

        if (changed)
        {
            Save(channel.Id, state);
        }

        return state;
    }

    private static Unit WeightedPick(List<Unit> candidates, IReadOnlyDictionary<Guid, double> weights)
    {
        var total = candidates.Sum(u => weights.TryGetValue(u.Id, out var x) ? x : 0.05);
        var roll = Random.Shared.NextDouble() * total;
        foreach (var u in candidates)
        {
            roll -= weights.TryGetValue(u.Id, out var x) ? x : 0.05;
            if (roll <= 0)
            {
                return u;
            }
        }

        return candidates[^1];
    }

    /// <summary>Pick the next program: shows rotate (no repeats within <see cref="ShowGap"/>), episodes shuffle across seasons, movies wait 2 days.</summary>
    private static List<Entry> Next(QuasiChannel channel, ChannelState state, DateTime start)
    {
        if (channel.InOrder)
        {
            var ordered = NextInOrder(channel, state, start);
            if (ordered is not null)
            {
                return new List<Entry> { ordered };
            }
        }

        var pool = channel.Pool;

        // Weighted channels only forbid back-to-back repeats (the 2-show gap would cap the top title at 33%).
        var gap = Math.Min(channel.Weights is null ? ShowGap : 1, Math.Max(0, pool.Count - 1));
        var recent = state.Recent.TakeLast(gap).ToHashSet();

        bool MovieOk(Unit u) => u.IsSeries || !state.LastAired.TryGetValue(u.Id.ToString("N"), out var t) || start - t >= MovieNoRepeat;

        var eligible = pool.Where(u => !recent.Contains(u.Id) && MovieOk(u)).ToList();
        Unit pick;
        if (eligible.Count > 0)
        {
            pick = channel.Weights is { } w ? WeightedPick(eligible, w) : eligible[Random.Shared.Next(eligible.Count)];
        }
        else
        {
            // Everything's been on recently: take whatever aired longest ago.
            var fallback = pool.Where(u => !recent.Contains(u.Id)).ToList();
            if (fallback.Count == 0)
            {
                fallback = pool.ToList();
            }

            pick = fallback.OrderBy(u => state.LastAired.TryGetValue(u.Id.ToString("N"), out var t) ? t : DateTime.MinValue).First();
        }

        var itemIds = pick.IsSeries ? DrawEpisodeBlock(state, pick.Id, channel.BlockFor(pick.Id)) : new List<Guid> { pick.Id };

        state.Recent.Add(pick.Id);
        if (state.Recent.Count > 20)
        {
            state.Recent.RemoveAt(0);
        }

        state.LastAired[pick.Id.ToString("N")] = start;

        var entries = new List<Entry>();
        var cursor = start;
        Guid? prevUnit = state.Entries.Count > 0 ? state.Entries[^1].UnitId : null;
        foreach (var itemId in itemIds)
        {
            var e = MakeEntry(itemId, pick.Id, cursor, prevUnit);
            entries.Add(e);
            cursor = cursor.AddTicks(e.Ticks);
            prevUnit = pick.Id;
        }

        return entries;
    }

    /// <summary>In-order mode: everything by release date (shows expanded to their episodes in order), looping.</summary>
    private static Entry? NextInOrder(QuasiChannel channel, ChannelState state, DateTime start)
    {
        var playlist = Playlist(channel);
        if (playlist.Count == 0)
        {
            return null;
        }

        var i = 0;
        if (state.LastOrderedItem is Guid last)
        {
            var found = playlist.FindIndex(p => p.ItemId == last);
            i = found >= 0 ? found + 1 : 0;
        }

        if (i >= playlist.Count)
        {
            i = 0; // loop
        }

        var (itemId, unitId) = playlist[i];
        state.LastOrderedItem = itemId;
        state.Recent.Add(unitId);
        if (state.Recent.Count > 20)
        {
            state.Recent.RemoveAt(0);
        }

        state.LastAired[unitId.ToString("N")] = start;
        Guid? prevUnit = state.Entries.Count > 0 ? state.Entries[^1].UnitId : null;
        return MakeEntry(itemId, unitId, start, prevUnit);
    }

    /// <summary>One schedule entry: optional bumper (new show/movie coming up) + the item, credits trimmed.</summary>
    private static Entry MakeEntry(Guid itemId, Guid unitId, DateTime start, Guid? prevUnit)
    {
        var runtime = TimeSpan.TicksPerMinute * 22;
        var play = runtime;
        var trimmed = false;
        if (_index!.Playable.TryGetValue(itemId, out var item))
        {
            runtime = item.RunTimeTicks is long r && r > 0 ? r : runtime;
            play = Credits.PlayTicks(item, runtime, out trimmed);
        }

        // Bumper only when a different show / movie comes up (not between episodes of the same show, not on a fresh start).
        var bumper = Config.ShowBumpers && prevUnit is Guid p && p != unitId
            ? TimeSpan.FromSeconds(Math.Clamp(Config.BumperSeconds, 2, 15)).Ticks
            : 0;

        return new Entry { ItemId = itemId, UnitId = unitId, Start = start, Ticks = bumper + play, BumperTicks = bumper, PlayTicks = play, Trimmed = trimmed };
    }

    private static List<(Guid ItemId, Guid UnitId)> Playlist(QuasiChannel channel)
    {
        static DateTime When(BaseItem i) => i.PremiereDate ?? (i.ProductionYear is int y && y > 0 ? new DateTime(y, 1, 1) : DateTime.MaxValue);

        var list = new List<(Guid ItemId, Guid UnitId)>();
        var units = channel.Pool
            .Select(u => (Unit: u, Item: u.IsSeries
                ? (_index!.SeriesById.TryGetValue(u.Id, out var s) ? (BaseItem)s : null)
                : (_index!.Playable.TryGetValue(u.Id, out var p) ? p : null)))
            .Where(x => x.Item is not null)
            .OrderBy(x => When(x.Item!))
            .ThenBy(x => x.Item!.SortName ?? x.Item!.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var (unit, item) in units)
        {
            if (unit.IsSeries && _index!.EpisodesBySeries.TryGetValue(unit.Id, out var eps))
            {
                list.AddRange(eps
                    .Where(e => (e.ParentIndexNumber ?? 1) > 0) // skip specials
                    .OrderBy(e => e.ParentIndexNumber ?? 1)
                    .ThenBy(e => e.IndexNumber ?? int.MaxValue)
                    .Select(e => (e.Id, unit.Id)));
            }
            else if (!unit.IsSeries)
            {
                list.Add((item!.Id, unit.Id));
            }
        }

        return list;
    }

    /// <summary>Shuffle-deck per show: every episode airs once before any repeats.</summary>
    private static readonly System.Text.RegularExpressions.Regex PartOne = new(
        @"\((1|one|i)\)\s*$|\bpart\s*(1|one|i)\b|\bpt\.?\s*1\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex PartTwoPlus = new(
        @"\((2|3|two|three|ii|iii)\)\s*$|\bpart\s*(2|3|two|three|ii|iii)\b|\bpt\.?\s*[23]\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Shuffle-deck per show (every episode airs before repeats), but play <paramref name="inARow"/> consecutive
    /// episodes from where the draw lands. Multi-part episodes ("Part 1/2", "(1)/(2)") are always kept together.
    /// </summary>
    private static List<Guid> DrawEpisodeBlock(ChannelState state, Guid seriesId, int inARow)
    {
        var key = seriesId.ToString("N");
        var ordered = (_index!.EpisodesBySeries.TryGetValue(seriesId, out var eps) ? eps : Array.Empty<MediaBrowser.Controller.Entities.TV.Episode>())
            .OrderBy(e => (e.ParentIndexNumber ?? 1) == 0 ? int.MaxValue : e.ParentIndexNumber ?? 1) // specials last
            .ThenBy(e => e.IndexNumber ?? int.MaxValue)
            .ToList();
        if (ordered.Count == 0)
        {
            return new List<Guid> { seriesId };
        }

        if (!state.Decks.TryGetValue(key, out var deck))
        {
            deck = new List<Guid>();
            state.Decks[key] = deck;
        }

        deck.RemoveAll(id => !_index.Playable.ContainsKey(id));

        // New episodes since last time join the current shuffle right away.
        var allIds = ordered.Select(e => e.Id).ToList();
        if (state.Known.TryGetValue(key, out var known) && deck.Count > 0)
        {
            var knownSet = known.ToHashSet();
            foreach (var fresh in allIds.Where(id => !knownSet.Contains(id) && !deck.Contains(id)))
            {
                deck.Insert(Random.Shared.Next(deck.Count + 1), fresh);
            }
        }

        if (deck.Count == 0)
        {
            deck.AddRange(allIds.OrderBy(_ => Random.Shared.Next()));
        }

        state.Known[key] = allIds;

        var first = ordered.FindIndex(e => e.Id == deck[0]);
        if (first < 0)
        {
            first = 0;
        }

        // Landed on a "Part 2"? Start at its Part 1 instead.
        while (first > 0 && PartTwoPlus.IsMatch(ordered[first].Name ?? string.Empty))
        {
            first--;
        }

        var block = new List<Guid>();
        for (var k = first; k < ordered.Count && block.Count < inARow + 3; k++)
        {
            if (block.Count >= inARow)
            {
                // Past the requested count: only continue to finish a multi-part story.
                var prev = ordered[k - 1].Name ?? string.Empty;
                var prevIsPart = PartOne.IsMatch(prev) || PartTwoPlus.IsMatch(prev);
                if (!prevIsPart || !PartTwoPlus.IsMatch(ordered[k].Name ?? string.Empty))
                {
                    break;
                }
            }

            block.Add(ordered[k].Id);
        }

        if (block.Count == 0)
        {
            block.Add(ordered[first].Id);
        }

        foreach (var id in block)
        {
            deck.Remove(id);
        }

        return block;
    }

    // ------------------------------------------------------------ persistence

    private static ChannelState Load(string channelId)
    {
        if (States.TryGetValue(channelId, out var s))
        {
            return s;
        }

        var path = StatePath(channelId);
        try
        {
            if (File.Exists(path))
            {
                s = JsonSerializer.Deserialize<ChannelState>(File.ReadAllText(path), Json);
            }
        }
        catch (Exception)
        {
            s = null; // corrupt: start over
        }

        s ??= new ChannelState();
        States[channelId] = s;
        return s;
    }

    private static void Save(string channelId, ChannelState state)
    {
        try
        {
            var path = StatePath(channelId);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state, Json));
        }
        catch (Exception)
        {
            // non-fatal: schedule stays in memory
        }
    }

    /// <summary>Kept next to the plugin's settings file so updates don't reset what's airing.</summary>
    private static string StatePath(string channelId)
        => Path.Combine(BaseDir(), "schedules", channelId + ".json");

    /// <summary>Plugin working folder next to the settings file (survives updates).</summary>
    public static string BaseDir()
        => Path.Combine(Path.GetDirectoryName(Plugin.Instance!.ConfigurationFilePath)!, "CablePackage");

    public sealed class ChannelState
    {
        public List<Entry> Entries { get; set; } = new();

        public Dictionary<string, List<Guid>> Decks { get; set; } = new();

        public Dictionary<string, DateTime> LastAired { get; set; } = new();

        public List<Guid> Recent { get; set; } = new();

        /// <summary>Last item played in in-order mode.</summary>
        public Guid? LastOrderedItem { get; set; }

        /// <summary>Episodes each show had when its deck was last filled (to spot new ones).</summary>
        public Dictionary<string, List<Guid>> Known { get; set; } = new();

        /// <summary>Content fingerprint the current plan was made from.</summary>
        public string? Signature { get; set; }
    }

    public sealed class Entry
    {
        public Guid ItemId { get; set; }

        public Guid UnitId { get; set; }

        public DateTime Start { get; set; }

        public long Ticks { get; set; }

        /// <summary>Length of the opening "Up Next" bumper (0 = none).</summary>
        public long BumperTicks { get; set; }

        /// <summary>How much of the item plays (0 = legacy entry: Ticks - BumperTicks).</summary>
        public long PlayTicks { get; set; }

        /// <summary>End credits cut short (fade out at the end).</summary>
        public bool Trimmed { get; set; }
    }
}
