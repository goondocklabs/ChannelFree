using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>
/// VH1 Pop-Up Video style trivia bubbles during music videos.
/// Facts come from a "&lt;video name&gt;.popup.txt" file next to the video (one fact per line, yours win),
/// else from Wikipedia (the song's article, then the artist's), fetched once and cached next to the plugin settings.
/// </summary>
public static partial class PopupVideo
{
    private const double ShowFor = 7;
    private const int WrapAt = 30;
    private const int MaxLines = 5;

    private static readonly ConcurrentDictionary<Guid, Task<IReadOnlyList<string>>> InFlight = new();
    private static readonly JsonSerializerOptions Json = new();
    private static readonly object SoundGate = new();
    private static readonly string[] Abbreviations = { "U.S.", "U.K.", "Jr.", "Sr.", "Mr.", "Mrs.", "Ms.", "Dr.", "St.", "No.", "vs.", "Vol.", "feat.", "ft.", "Inc.", "Co.", "Ltd.", "Bros.", "Mt." };

    private static Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static string CacheDir => Path.Combine(ChannelScheduler.BaseDir(), "popup");

    // ------------------------------------------------------------ facts

    /// <summary>Facts for a music video. Waits at most <paramref name="wait"/> (then plays without bubbles; the fetch keeps going for next time).</summary>
    public static async Task<IReadOnlyList<string>> FactsAsync(BaseItem item, IHttpClientFactory http, ILogger logger, TimeSpan wait, CancellationToken ct)
    {
        var own = Sidecar(item);
        if (own.Count > 0)
        {
            return own;
        }

        var task = Start(item, http, logger);
        var done = await Task.WhenAny(task, Task.Delay(wait, ct)).ConfigureAwait(false);
        return done == task && task.IsCompletedSuccessfully ? task.Result : Array.Empty<string>();
    }

    /// <summary>Look facts up ahead of time for videos coming up soon.</summary>
    public static void Prefetch(IEnumerable<BaseItem> items, IHttpClientFactory http, ILogger logger)
    {
        foreach (var item in items)
        {
            if (Sidecar(item).Count == 0)
            {
                _ = Start(item, http, logger);
            }
        }
    }

    private static Task<IReadOnlyList<string>> Start(BaseItem item, IHttpClientFactory http, ILogger logger)
    {
        // Finished lookups live in the cache file; only lookups still running are shared.
        if (InFlight.TryGetValue(item.Id, out var old) && old.IsCompleted)
        {
            InFlight.TryRemove(item.Id, out _);
        }

        return InFlight.GetOrAdd(item.Id, _ => Task.Run(() => Lookup(item, http, logger)));
    }

    private static IReadOnlyList<string> Sidecar(BaseItem item)
    {
        try
        {
            if (string.IsNullOrEmpty(item.Path))
            {
                return Array.Empty<string>();
            }

            var file = Path.Combine(Path.GetDirectoryName(item.Path) ?? string.Empty, Path.GetFileNameWithoutExtension(item.Path) + ".popup.txt");
            if (!File.Exists(file))
            {
                return Array.Empty<string>();
            }

            return File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();
        }
        catch (Exception)
        {
            return Array.Empty<string>();
        }
    }

    private sealed class CacheEntry
    {
        public DateTime FetchedUtc { get; set; }

        public List<string> Facts { get; set; } = new();
    }

    private static async Task<IReadOnlyList<string>> Lookup(BaseItem item, IHttpClientFactory http, ILogger logger)
    {
        var file = Path.Combine(CacheDir, item.Id.ToString("N") + ".json");
        try
        {
            if (File.Exists(file))
            {
                var cached = JsonSerializer.Deserialize<CacheEntry>(await File.ReadAllTextAsync(file).ConfigureAwait(false), Json);
                // Found facts: keep them. Found nothing: try again after a week (articles get written).
                if (cached is not null && (cached.Facts.Count > 0 || DateTime.UtcNow - cached.FetchedUtc < TimeSpan.FromDays(7)))
                {
                    return cached.Facts;
                }
            }
        }
        catch (Exception)
        {
            // re-fetch
        }

        var artist = ChannelRules.Artist(item);
        var song = item.Name ?? string.Empty;
        var facts = new List<string>();
        try
        {
            using var client = http.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("ChannelFree/1.0 (Jellyfin plugin; github.com/goondocklabs/ChannelFree)");

            if (song.Length > 0)
            {
                var intro = await FindIntro(
                    client,
                    $"{SongTitle(song)} {artist} song",
                    t => Key(t).Contains(Key(SongTitle(song)), StringComparison.Ordinal),
                    text => Mentions(text, "song", "single", "track", "ballad")).ConfigureAwait(false);
                facts.AddRange(Sentences(intro));
            }

            if (artist.Length > 0)
            {
                // "Nirvana" the band, not Nirvana the Buddhist idea: the article has to be about music.
                var intro = await FindIntro(
                    client,
                    $"{artist} band singer musician",
                    t => Key(t).StartsWith(Key(artist), StringComparison.Ordinal),
                    text => Mentions(text, "band", "singer", "rapper", "musician", "songwriter", "duo", "group", "DJ", "producer", "trio")).ConfigureAwait(false);
                facts.AddRange(Sentences(intro).Take(4));
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "QuasiTV: no Pop-Up facts for {Title} (Wikipedia unreachable?)", song);
            return facts; // not cached: try again next time
        }

        facts = facts.Distinct(StringComparer.OrdinalIgnoreCase).Take(10).ToList();
        try
        {
            Directory.CreateDirectory(CacheDir);
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new CacheEntry { FetchedUtc = DateTime.UtcNow, Facts = facts }, Json)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // cache is optional
        }

        logger.LogInformation("QuasiTV: {Count} Pop-Up facts for {Artist} - {Title}", facts.Count, artist, song);
        return facts;
    }

    /// <summary>Intro text of the first search result whose title and opening both look right; "" if none.</summary>
    private static async Task<string> FindIntro(HttpClient client, string query, Func<string, bool> titleOk, Func<string, bool> textOk)
    {
        foreach (var title in (await FindArticles(client, query).ConfigureAwait(false)).Where(titleOk).Take(3))
        {
            var intro = await Intro(client, title).ConfigureAwait(false);
            if (textOk(intro.Length > 400 ? intro[..400] : intro))
            {
                return intro;
            }
        }

        return string.Empty;
    }

    private static bool Mentions(string text, params string[] words)
        => words.Any(w => Regex.IsMatch(text, @"\b" + Regex.Escape(w) + @"s?\b", RegexOptions.IgnoreCase));

    private static async Task<List<string>> FindArticles(HttpClient client, string query)
    {
        var titles = new List<string>();
        var url = "https://en.wikipedia.org/w/api.php?action=query&list=search&format=json&formatversion=2&srlimit=5&srsearch=" + Uri.EscapeDataString(query);
        using var doc = JsonDocument.Parse(await client.GetStringAsync(url).ConfigureAwait(false));
        if (!doc.RootElement.TryGetProperty("query", out var q) || !q.TryGetProperty("search", out var results))
        {
            return titles;
        }

        foreach (var r in results.EnumerateArray())
        {
            var title = r.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (title is null || title.Contains("discography", StringComparison.OrdinalIgnoreCase) || title.StartsWith("List of", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            titles.Add(title);
        }

        return titles;
    }

    private static async Task<string> Intro(HttpClient client, string title)
    {
        var url = "https://en.wikipedia.org/w/api.php?action=query&prop=extracts&exintro=1&explaintext=1&redirects=1&format=json&formatversion=2&titles=" + Uri.EscapeDataString(title);
        using var doc = JsonDocument.Parse(await client.GetStringAsync(url).ConfigureAwait(false));
        if (doc.RootElement.TryGetProperty("query", out var q) && q.TryGetProperty("pages", out var pages))
        {
            foreach (var p in pages.EnumerateArray())
            {
                if (p.TryGetProperty("extract", out var e))
                {
                    return e.GetString() ?? string.Empty;
                }
            }
        }

        return string.Empty;
    }

    /// <summary>Bubble-sized sentences from an article intro.</summary>
    internal static IEnumerable<string> Sentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        // Drop pronunciation guides and other long asides in parentheses.
        text = AsideRegex().Replace(text, m => m.Value.Length > 28 || m.Value.Any(c => c > 0x2000 && c < 0x2BFF) || m.Value.Contains(';', StringComparison.Ordinal) ? string.Empty : m.Value);
        text = SpaceRegex().Replace(text.Replace('\n', ' '), " ").Replace(" ,", ",", StringComparison.Ordinal).Replace(" .", ".", StringComparison.Ordinal);

        var start = 0;
        for (var i = 0; i < text.Length - 2; i++)
        {
            var c = text[i];
            if ((c != '.' && c != '!' && c != '?') || text[i + 1] != ' ' || !(char.IsUpper(text[i + 2]) || char.IsDigit(text[i + 2]) || text[i + 2] == '"'))
            {
                continue;
            }

            var wordStart = text.LastIndexOf(' ', i) + 1;
            var word = text[wordStart..(i + 1)];
            if (Abbreviations.Contains(word, StringComparer.Ordinal) || InitialsRegex().IsMatch(word))
            {
                continue;
            }

            var s = text[start..(i + 1)].Trim();
            start = i + 2;
            if (s.Length is >= 30 and <= 145)
            {
                yield return s;
            }
        }

        var last = text[start..].Trim();
        if (last.Length is >= 30 and <= 145)
        {
            yield return last;
        }
    }

    // ------------------------------------------------------------ drawing

    /// <summary>Bubbles for this run: extra filter chain (starting with ",") and the times (seconds into the run) they pop up.</summary>
    public static (string Filter, List<double> Pops) Build(IReadOnlyList<string> facts, TimeSpan itemOffset, TimeSpan play, string workDir)
    {
        var pops = new List<double>();
        var fonts = Banner.FontDir();
        if (fonts is null || facts.Count == 0)
        {
            return (string.Empty, pops);
        }

        var len = play.TotalSeconds;
        var off = itemOffset.TotalSeconds;
        const double First = 16; // after the opening caption
        var last = len - 24;     // before the closing caption
        if (last - First < 8)
        {
            return (string.Empty, pops);
        }

        var max = Math.Clamp(Config.PopupVideoMaxPerVideo, 1, 12);
        var count = Math.Min(Math.Min(facts.Count, max), (int)((last - First) / 14) + 1);
        var gap = count > 1 ? (last - First) / (count - 1) : 0;

        var inv = CultureInfo.InvariantCulture;
        var regular = Path.Combine(fonts, "DejaVuSans.ttf");
        Directory.CreateDirectory(workDir);

        // Where bubbles go, in turn (never the lower left, where the artist/song caption lives).
        var spots = new[]
        {
            ("iw*0.05", "ih*0.07", "w*0.05", "h*0.07"),
            ("iw-ih*0.62-iw*0.05", "ih*0.07", "w-h*0.62-w*0.05", "h*0.07"),
            ("iw-ih*0.62-iw*0.05", "ih*0.36", "w-h*0.62-w*0.05", "h*0.36"),
            ("iw*0.05", "ih*0.30", "w*0.05", "h*0.30")
        };

        var sb = new StringBuilder();
        for (var i = 0; i < count; i++)
        {
            var at = First + (i * gap);
            var a = at - off;
            var b = at + ShowFor - off;
            if (b <= 0.5)
            {
                continue; // already over when this run starts
            }

            a = Math.Max(0, a);
            var lines = Wrap(facts[i]);
            var boxH = (lines.Count * 0.039) + 0.05;
            var (bx, by, tx, ty) = spots[i % spots.Length];
            var on = string.Format(inv, ":enable='between(t,{0:0.00},{1:0.00})'", a, b);
            string Box(string x, string y, string w, string h, string color) => $",drawbox=x={x}:y={y}:w={w}:h={h}:color={color}:t=fill{on}";
            var hh = boxH.ToString("0.000", inv);

            // shadow, chamfered white bubble (two overlapping boxes), and the little "thought" dots
            sb.Append(Box($"{bx}+ih*0.008", $"{by}+ih*0.008", "ih*0.62", $"ih*{hh}", "black@0.35"));
            sb.Append(Box(bx, $"{by}+ih*0.012", "ih*0.62", $"ih*{hh}-ih*0.024", "white@0.97"));
            sb.Append(Box($"{bx}+ih*0.012", by, "ih*0.596", $"ih*{hh}", "white@0.97"));
            sb.Append(Box($"{bx}+ih*0.09", $"{by}+ih*{(boxH + 0.012).ToString("0.000", inv)}", "ih*0.032", "ih*0.032", "white@0.97"));
            sb.Append(Box($"{bx}+ih*0.06", $"{by}+ih*{(boxH + 0.056).ToString("0.000", inv)}", "ih*0.018", "ih*0.018", "white@0.97"));

            var f = Path.Combine(workDir, $"pop{i}.txt");
            File.WriteAllText(f, string.Join('\n', lines), new UTF8Encoding(false));
            sb.Append(CultureInfo.InvariantCulture, $",drawtext=fontfile={regular}:textfile={f}:expansion=none:fontcolor=0x1A1A2E:fontsize=h*0.032:x={tx}+h*0.028:y={ty}+h*0.026{on}");

            if (at - off >= 0.3)
            {
                pops.Add(a);
            }
        }

        return (sb.ToString(), pops);
    }

    private static List<string> Wrap(string text)
    {
        var lines = new List<string>();
        var line = new StringBuilder();
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > WrapAt)
            {
                lines.Add(line.ToString());
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            lines.Add(line.ToString());
        }

        if (lines.Count > MaxLines)
        {
            lines = lines.Take(MaxLines).ToList();
            lines[^1] = lines[^1].TrimEnd('.', ',', ';') + "…";
        }

        return lines;
    }

    /// <summary>The "bloop" played as each bubble pops up (bundled; written next to the plugin settings once).</summary>
    public static string? Sound()
    {
        lock (SoundGate)
        {
            try
            {
                var file = Path.Combine(CacheDir, "popup.wav");
                if (!File.Exists(file))
                {
                    var asm = typeof(Plugin).Assembly;
                    var res = asm.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith(".popup.wav", StringComparison.OrdinalIgnoreCase));
                    if (res is null)
                    {
                        return null;
                    }

                    Directory.CreateDirectory(CacheDir);
                    var tmp = file + ".tmp";
                    using (var src = asm.GetManifestResourceStream(res)!)
                    using (var dst = File.Create(tmp))
                    {
                        src.CopyTo(dst);
                    }

                    File.Move(tmp, file, true);
                }

                return file;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    // ------------------------------------------------------------ helpers

    /// <summary>Song name without "(Official Video)", "[HD]", "(Remastered)" and the like.</summary>
    private static string SongTitle(string name)
    {
        var s = BracketRegex().Replace(name, string.Empty).Trim();
        return s.Length > 0 ? s : name;
    }

    private static string Key(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.Replace('’', '\'').Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(char.ToLowerInvariant(c));
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\s*\([^()]*\)")]
    private static partial Regex AsideRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"^([A-Za-z]\.)+$")]
    private static partial Regex InitialsRegex();

    [GeneratedRegex(@"\s*[\(\[][^\)\]]*[\)\]]")]
    private static partial Regex BracketRegex();
}
