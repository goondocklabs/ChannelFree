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
    private const int WrapAt = 28;
    private const int MaxLines = 4;

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

    /// <summary>One bubble picture and when/where it shows (seconds into this run).</summary>
    public sealed record Bubble(string Image, string Place, double From, double To);

    // Badge on the left of each bubble, in turn: glyph + colour.
    private static readonly (string Glyph, int R, int G, int B)[] Badges =
    {
        ("?", 0xE8, 0x45, 0x7A),
        ("!", 0x2F, 0x7D, 0xE1),
        ("♪", 0xF2, 0x8C, 0x28),
        ("★", 0x2B, 0xB6, 0x73)
    };

    // Where bubbles go, in turn (overlay x:y). Bubbles only show mid-song, so the lower left is free.
    private static readonly string[] Spots =
    {
        "x=main_w*0.05:y=main_h-overlay_h-main_h*0.08",
        "x=main_w-overlay_w-main_w*0.05:y=main_h*0.08",
        "x=main_w*0.05:y=main_h*0.08",
        "x=main_w-overlay_w-main_w*0.05:y=main_h-overlay_h-main_h*0.08"
    };

    /// <summary>Draws this run's bubbles as transparent pictures sized for a <paramref name="height"/>-line picture,
    /// and returns them with the times (seconds into the run) they pop up.</summary>
    public static async Task<(List<Bubble> Bubbles, List<double> Pops)> BuildAsync(
        string ffmpeg, IReadOnlyList<string> facts, TimeSpan itemOffset, TimeSpan play, int height, string workDir, ILogger logger, CancellationToken ct)
    {
        var bubbles = new List<Bubble>();
        var pops = new List<double>();
        var fonts = Banner.FontDir();
        if (fonts is null || facts.Count == 0)
        {
            return (bubbles, pops);
        }

        var len = play.TotalSeconds;
        var off = itemOffset.TotalSeconds;
        const double First = 16; // after the opening caption
        var last = len - 24;     // before the closing caption
        if (last - First < 8)
        {
            return (bubbles, pops);
        }

        var max = Math.Clamp(Config.PopupVideoMaxPerVideo, 1, 12);
        var count = Math.Min(Math.Min(facts.Count, max), (int)((last - First) / 14) + 1);
        var gap = count > 1 ? (last - First) / (count - 1) : 0;
        var bold = Path.Combine(fonts, "DejaVuSans-Bold.ttf");
        Directory.CreateDirectory(workDir);

        for (var i = 0; i < count; i++)
        {
            var at = First + (i * gap);
            var a = at - off;
            var b = at + ShowFor - off;
            if (b <= 0.5)
            {
                continue; // already over when this run starts
            }

            var file = Path.Combine(workDir, $"bubble{i}.png");
            if (!await Draw(ffmpeg, facts[i], Badges[i % Badges.Length], Math.Clamp(height, 240, 1080), bold, workDir, i, file, logger, ct).ConfigureAwait(false))
            {
                continue;
            }

            bubbles.Add(new Bubble(file, Spots[i % Spots.Length], Math.Max(0, a), b));
            if (a >= 0.3)
            {
                pops.Add(a);
            }
        }

        return (bubbles, pops);
    }

    /// <summary>White rounded bubble with a dark outline, bold text, and a round badge on the left (one transparent PNG).</summary>
    private static Task<bool> Draw(string ffmpeg, string fact, (string Glyph, int R, int G, int B) badge, int h, string bold, string dir, int n, string file, ILogger logger, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var lines = Wrap(fact);
        var fs = (int)Math.Round(0.034 * h);
        var lh = (int)Math.Round(fs * 1.25);
        var pad = (int)Math.Round(0.02 * h);
        var ol = Math.Max(3, (int)Math.Round(h / 170.0));   // outline
        var ri = (int)Math.Round(0.05 * h);                 // badge radius
        var ring = Math.Max(3, (int)Math.Round(h / 150.0)); // white ring inside the badge
        var textW = (int)Math.Round(lines.Max(l => l.Length) * 0.64 * fs);
        var hb = Math.Max((lines.Count * lh) + (2 * pad), (2 * ri) + ol);
        var x0 = ri + ol;
        var tx = x0 + ri + (int)Math.Round(0.018 * h);
        var wc = tx + textW + pad + ol + 2;
        var hc = hb + (2 * ol) + 2;
        var y0 = ol + 1;
        var x1 = wc - ol - 2;
        var y1 = hc - ol - 2;
        var r = Math.Min(hb / 2, (int)Math.Round(0.03 * h));
        var cx = ri + ol + 1;
        var cy = hc / 2;

        // Signed distances: dB to the bubble edge, dI to the badge edge (negative = inside).
        var dB = string.Format(inv, "hypot(max(max({0}+{2}-X,X-({1}-{2})),0),max(max({3}+{2}-Y,Y-({4}-{2})),0))-{2}", x0, x1, r, y0, y1);
        var dI = string.Format(inv, "hypot(X-{0},Y-{1})-{2}", cx, cy, ri);
        string Channel(int core, int white, int dark) => string.Format(
            inv,
            "'st(0,{0});st(1,{1});if(lte(ld(1),-{2}),{3},if(lte(ld(1),0),{4},if(lte(ld(1),{5}),{6},if(lte(ld(0),0),{4},{6}))))'",
            dB, dI, ring, core, white, ol, dark);
        var alpha = string.Format(inv, "'st(0,{0});st(1,{1});255*clip(max({2}-ld(1),{2}-ld(0))+0.5,0,1)'", dB, dI, ol);

        var textFile = Path.Combine(dir, $"bubble{n}.txt");
        var glyphFile = Path.Combine(dir, $"badge{n}.txt");
        File.WriteAllText(textFile, string.Join('\n', lines), new UTF8Encoding(false));
        File.WriteAllText(glyphFile, badge.Glyph, new UTF8Encoding(false));

        var vf = string.Join(
            ",",
            "format=rgba",
            $"geq=r={Channel(badge.R, 255, 20)}:g={Channel(badge.G, 255, 20)}:b={Channel(badge.B, 255, 20)}:a={alpha}",
            string.Format(inv, "drawtext=fontfile={0}:textfile={1}:expansion=none:fontsize={2}:fontcolor=white:x={3}-tw/2:y={4}-th/2", bold, glyphFile, (int)Math.Round(ri * 1.15), cx, cy),
            string.Format(inv, "drawtext=fontfile={0}:textfile={1}:expansion=none:fontsize={2}:line_spacing={3}:fontcolor=0x111111:x={4}:y=(h-th)/2", bold, textFile, fs, lh - fs, tx));

        return GuideChannel.Render(
            ffmpeg,
            new[] { "-f", "lavfi", "-i", string.Format(inv, "color=c=black@0:s={0}x{1}:d=1", wc, hc), "-vf", vf, "-frames:v", "1", file },
            logger,
            ct);
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
