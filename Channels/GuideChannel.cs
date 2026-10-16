using System.Diagnostics;
using System.Globalization;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>The pictures one guide run needs: the static screen, the tall channel grid, and the "Up next" panels.</summary>
public sealed record GuideAssets(string Background, string Grid, int GridHeight, IReadOnlyList<string> Promos);

/// <summary>
/// Prevue style guide channel: the bottom half is a grid of every channel (now and the next hour) that scrolls by
/// on its own, the top half rotates "Up next" panels with the coming shows' art, plus a live clock.
/// The grid and panels are drawn once per run as still pictures (cheap); the live stream only scrolls and swaps them.
/// </summary>
public static class GuideChannel
{
    public const string Id = "quasi-guide";
    public const string Name = "Guide";

    private const int W = 1280;
    private const int H = 720;
    private const int GridY = 402;
    private const int GridH = H - GridY;
    private const int RowH = 72;
    private const int X0 = 176;
    private const int X1 = 1274;
    private const int WindowMinutes = 90;
    private const int PromoSeconds = 8;
    private const int MaxPromos = 6;

    private const string Bg = "0x080A28";
    private const string Panel = "0x121E78";
    private const string Edge = "0xE6E6FF";
    private const string Gold = "0xFFD228";
    private const string GridBg = "0x0A145A";
    private const string Chan = "0x1E32AA";
    private const string ChanEdge = "0x96AAFF";
    private const string Cell = "0x16248C";
    private const string CellEdge = "0x6E82E6";
    private const string Cream = "0xFFE178";
    private const string Ink = "0x14143C";

    /// <summary>Length of the built-in loop (32 bars at 92 BPM).</summary>
    private const double BuiltInLoopSeconds = 32 * 4 * 60 / 92.0;

    private static readonly object MusicGate = new();
    private static readonly string[] MusicExt = { ".mp3", ".m4a", ".aac", ".ogg", ".flac", ".wav", ".opus" };
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private static Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;

    public static bool Enabled => Config.GuideEnabled;

    public static int Number => Math.Max(1, Config.GuideNumber);

    /// <summary>Pixels per second the grid moves.</summary>
    public static int ScrollSpeed => (Config.GuideScrollSpeed ?? "normal").ToLowerInvariant() switch
    {
        "slow" => 24,
        "fast" => 60,
        _ => 40
    };

    /// <summary>When the current run should end: the next :00 / :30 (the grid moves on), at most 10 minutes away
    /// (so "Up next" stays fresh), at least 20 seconds away.</summary>
    public static DateTime RunEnd(DateTime nowUtc)
    {
        var tz = Banner.ResolveTimeZone(Config.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(nowUtc, tz);
        var slotStart = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute >= 30 ? 30 : 0, 0, DateTimeKind.Unspecified);
        DateTime boundary;
        try
        {
            boundary = TimeZoneInfo.ConvertTimeToUtc(slotStart.AddMinutes(30), tz);
        }
        catch (ArgumentException)
        {
            boundary = nowUtc.AddMinutes(10); // daylight saving gap
        }

        var end = boundary < nowUtc.AddMinutes(10) ? boundary : nowUtc.AddMinutes(10);
        return end < nowUtc.AddSeconds(20) ? nowUtc.AddSeconds(20) : end;
    }

    /// <summary>Seconds to add to the stream's clock so ffmpeg's gmtime shows local wall time.</summary>
    public static long LocalEpoch(DateTime nowUtc)
    {
        var tz = Banner.ResolveTimeZone(Config.TimeZoneId);
        var offset = tz.GetUtcOffset(nowUtc);
        return (long)Math.Floor((nowUtc - DateTime.UnixEpoch + offset).TotalSeconds);
    }

    // ------------------------------------------------------------ drawing the stills

    /// <summary>Draw everything for a run that starts at <paramref name="atUtc"/>. Null if ffmpeg or the fonts aren't available.</summary>
    public static async Task<GuideAssets?> BuildAsync(ILibraryManager library, string ffmpeg, DateTime atUtc, string title, string dir, ILogger logger, CancellationToken ct)
    {
        var fonts = Banner.FontDir();
        if (fonts is null)
        {
            return null;
        }

        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, true);
        }

        Directory.CreateDirectory(dir);
        var bold = Path.Combine(fonts, "DejaVuSans-Bold.ttf");
        var pen = new Pen(dir, bold);

        var tz = Banner.ResolveTimeZone(Config.TimeZoneId);
        var local = TimeZoneInfo.ConvertTimeFromUtc(atUtc, tz);
        var windowLocal = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute >= 30 ? 30 : 0, 0, DateTimeKind.Unspecified);
        DateTime windowUtc;
        try
        {
            windowUtc = TimeZoneInfo.ConvertTimeToUtc(windowLocal, tz);
        }
        catch (ArgumentException)
        {
            windowUtc = atUtc.AddMinutes(-(local.Minute % 30));
        }

        var channels = ChannelScheduler.GetChannels(library);

        // ---- the static screen: right panel (name, date), time bar, empty grid area
        var bgFile = Path.Combine(dir, "background.png");
        var b = new List<string>
        {
            Box(900, 20, 360, 320, Panel),
            Box(900, 20, 360, 320, Edge, 3),
            pen.Text(Fit(title.ToUpperInvariant(), 330, 44, 0.74), 44, Gold, "1080-tw/2", "30"),
            pen.Text("CHANNEL GUIDE", 22, "white", "1080-tw/2", "82"),
            Box(930, 119, 300, 2, "0x788CE6"),
            pen.Text(local.ToString("dddd  MMM d", Us).ToUpperInvariant(), 22, Cream, "1080-tw/2", "290"),
            Box(0, 360, W, 40, Gold),
            pen.Text(local.Hour >= 17 || local.Hour < 4 ? "TONIGHT" : "TODAY", 22, Ink, "14", "368"),
            Box(0, GridY, W, GridH, GridBg)
        };
        var ppm = (X1 - X0) / (double)WindowMinutes;
        for (var i = 0; i < 3; i++)
        {
            var label = windowLocal.AddMinutes(30 * i).ToString("h:mm tt", Us);
            b.Add(pen.Text(label, 22, Ink, ((int)(X0 + (i * 30 * ppm) + 10)).ToString(Inv), "368"));
        }

        if (!await Render(ffmpeg, new[] { "-f", "lavfi", "-i", $"color=c={Bg}:s={W}x{H}:d=1", "-vf", string.Join(",", b), "-frames:v", "1", bgFile }, logger, ct).ConfigureAwait(false))
        {
            return null;
        }

        // ---- the channel grid (one row per channel, all rows in one tall picture)
        var rows = channels.Select(ch => (ch, Cells(ch, windowUtc))).ToList();
        var gridH = Math.Max(1, rows.Count) * RowH;
        var g = new List<string>();
        for (var r = 0; r < rows.Count; r++)
        {
            var (ch, cells) = rows[r];
            var y = (r * RowH) + 2;
            var ys = y.ToString(Inv);
            g.Add(Box(6, y, 164, RowH - 6, Chan));
            g.Add(Box(6, y, 164, RowH - 6, ChanEdge, 2));
            g.Add(pen.Text(ch.Number.ToString(Inv), 30, "white", "88-tw/2", (y + 6).ToString(Inv)));
            var name = Fit(ch.Name.ToUpperInvariant(), 150, 16, 0.74);
            if (name.Length > 0)
            {
                g.Add(pen.Text(name, 16, Cream, "88-tw/2", (y + 42).ToString(Inv)));
            }

            foreach (var (a, e, text) in cells)
            {
                var xa = (int)(X0 + (Math.Max(a, 0) * ppm));
                var xb = (int)(X0 + (Math.Min(e, WindowMinutes) * ppm)) - 4;
                if (xb - xa < 8)
                {
                    continue;
                }

                g.Add(Box(xa, y, xb - xa, RowH - 6, Cell));
                g.Add(Box(xa, y, xb - xa, RowH - 6, CellEdge, 2));
                var tx = xa + 12;
                var room = xb - xa - 24;
                if (a < 0)
                {
                    g.Add(pen.Text("◀", 18, Cream, tx.ToString(Inv), (y + 12).ToString(Inv)));
                    tx += 22;
                    room -= 22;
                }

                if (e > WindowMinutes)
                {
                    g.Add(pen.Text("▶", 18, Cream, (xb - 28).ToString(Inv), (y + 12).ToString(Inv)));
                    room -= 22;
                }

                var t = Fit(text, room, 22, 0.62);
                if (t.Length > 0)
                {
                    g.Add(pen.Text(t, 22, "white", tx.ToString(Inv), (y + 10).ToString(Inv)));
                }
            }
        }

        var gridFile = Path.Combine(dir, "grid.png");
        var gridArgs = new List<string> { "-f", "lavfi", "-i", $"color=c={GridBg}:s={W}x{gridH}:d=1" };
        if (g.Count > 0)
        {
            // Long lineups make a long filter; a script file keeps the command line short.
            var script = Path.Combine(dir, "grid.filter");
            await File.WriteAllTextAsync(script, string.Join(",\n", g), new UTF8Encoding(false), ct).ConfigureAwait(false);
            gridArgs.AddRange(new[] { "-filter_script:v", script });
        }

        gridArgs.AddRange(new[] { "-frames:v", "1", gridFile });
        if (!await Render(ffmpeg, gridArgs, logger, ct).ConfigureAwait(false))
        {
            return null;
        }

        // ---- "Up next" panels
        var promos = new List<string>();
        var picks = Promos(channels, atUtc, out var label2);
        foreach (var (ch, slot) in picks)
        {
            var file = Path.Combine(dir, $"promo{promos.Count}.png");
            var at = TimeZoneInfo.ConvertTimeFromUtc(slot.ItemStartUtc, tz).ToString("h:mm tt", Us);
            var line = label2 == "UP NEXT"
                ? $"{at}   on   {ch.Number}  {ch.Name.ToUpperInvariant()}"
                : $"Now on   {ch.Number}  {ch.Name.ToUpperInvariant()}";
            if (await Promo(ffmpeg, slot.Item, label2, Title(slot.Item), Fit(line, 800, 22, 0.66), file, pen, logger, ct).ConfigureAwait(false))
            {
                promos.Add(file);
            }
        }

        if (promos.Count == 0)
        {
            var file = Path.Combine(dir, "promo0.png");
            if (await Promo(ffmpeg, null, "ON THE AIR", title, $"{channels.Count} channels", file, pen, logger, ct).ConfigureAwait(false))
            {
                promos.Add(file);
            }
        }

        return new GuideAssets(bgFile, gridFile, gridH, promos);
    }

    /// <summary>One channel's cells in the 90 minute window: (start minute, end minute, text). Minutes can run past the window edges.</summary>
    private static List<(double A, double E, string Text)> Cells(QuasiChannel ch, DateTime windowUtc)
    {
        var slots = ChannelScheduler.Slots(ch, windowUtc, windowUtc.AddMinutes(WindowMinutes));
        var cells = new List<(double A, double E, string Text)>();
        if (slots.Count == 0)
        {
            return cells;
        }

        // A music video channel is one long cell: "Music Videos: artist, artist, ..."
        if (slots.All(s => s.Item is MusicVideo))
        {
            var artists = slots.Select(s => ChannelRules.Artist(s.Item)).Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(8).ToList();
            var text = artists.Count > 0 ? "Music Videos: " + string.Join(", ", artists) : "Music Videos";
            cells.Add(((slots[0].StartUtc - windowUtc).TotalMinutes, (slots[slots.Count - 1].EndUtc - windowUtc).TotalMinutes, text));
            return cells;
        }

        foreach (var s in slots)
        {
            var a = (s.StartUtc - windowUtc).TotalMinutes;
            var e = (s.EndUtc - windowUtc).TotalMinutes;
            var text = Title(s.Item);
            if (cells.Count > 0 && string.Equals(cells[^1].Text, text, StringComparison.Ordinal))
            {
                cells[^1] = (cells[^1].A, e, text); // back to back episodes of one show: one cell
            }
            else
            {
                cells.Add((a, e, text));
            }
        }

        return cells;
    }

    /// <summary>What each channel starts next (skipping music videos), soonest first; or what's on now if nothing is coming.</summary>
    private static List<(QuasiChannel Ch, Slot Slot)> Promos(IReadOnlyList<QuasiChannel> channels, DateTime atUtc, out string label)
    {
        var next = new List<(QuasiChannel Ch, Slot Slot)>();
        var now = new List<(QuasiChannel Ch, Slot Slot)>();
        foreach (var ch in channels)
        {
            var slots = ChannelScheduler.Slots(ch, atUtc, atUtc.AddMinutes(75));
            var cur = slots.FirstOrDefault(s => s.StartUtc <= atUtc);
            if (cur is not null && cur.Item is not MusicVideo)
            {
                now.Add((ch, cur));
            }

            var up = slots.FirstOrDefault(s => s.StartUtc > atUtc && s.Item is not MusicVideo);
            if (up is not null)
            {
                next.Add((ch, up));
            }
        }

        var pick = next.Count > 0 ? next : now;
        label = next.Count > 0 ? "UP NEXT" : "ON NOW";
        return pick.OrderBy(p => p.Slot.StartUtc)
            .GroupBy(p => Title(p.Slot.Item), StringComparer.OrdinalIgnoreCase)
            .Select(x => x.First())
            .Take(MaxPromos)
            .ToList();
    }

    private static async Task<bool> Promo(string ffmpeg, BaseItem? item, string label, string title, string line, string file, Pen pen, ILogger logger, CancellationToken ct)
    {
        var f = new List<string>();
        var args = new List<string>();
        var art = item is null ? null : Bumper.Backdrop(item);
        if (art is not null)
        {
            args.AddRange(new[] { "-i", art });
            f.Add("scale=860:320:force_original_aspect_ratio=increase,crop=860:320,setsar=1,format=rgb24");
        }
        else
        {
            args.AddRange(new[] { "-f", "lavfi", "-i", "gradients=s=860x320:c0=0x000B25:c1=0x7A4A9E:x0=0:y0=320:x1=860:y1=0:d=1" });
        }

        for (var i = 0; i < 8; i++)
        {
            f.Add(Box(0, 150 + (i * 22), 860, 22, "black@" + (0.12 + (i * 0.09)).ToString("0.00", Inv)));
        }

        var labelW = (int)Math.Round((label.Length * 22 * 0.72) + 24);
        f.Add(Box(20, 200, labelW, 32, Gold));
        f.Add(pen.Text(label, 22, Ink, "32", "205"));
        f.Add(pen.Text(Fit(title, 800, 40, 0.64), 40, "white", "20", "238", ":borderw=2:bordercolor=black"));
        f.Add(pen.Text(line, 22, Cream, "22", "290", ":borderw=1:bordercolor=black"));
        f.Add(Box(0, 0, 860, 320, Edge, 3));
        args.AddRange(new[] { "-vf", string.Join(",", f), "-frames:v", "1", file });
        return await Render(ffmpeg, args, logger, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------ the live picture

    /// <summary>ffmpeg inputs + filter graph for one run (output labels [bv] video, [ba] audio).</summary>
    public static List<string> LiveArgs(GuideAssets assets, string workDir, long localEpoch, double burstSeconds, out bool hasMusic)
    {
        var fonts = Banner.FontDir() ?? string.Empty;
        var bold = Path.Combine(fonts, "DejaVuSans-Bold.ttf");
        var args = new List<string> { "-i", assets.Background, "-i", assets.Grid };
        foreach (var p in assets.Promos)
        {
            args.AddRange(new[] { "-i", p });
        }

        var music = PickMusic(out var builtIn);
        hasMusic = music is not null;
        if (music is not null && builtIn)
        {
            // The built-in loop picks up where the clock says it should be, so runs join up musically.
            var at = (localEpoch % BuiltInLoopSeconds).ToString("0.000", Inv);
            args.AddRange(new[] { "-stream_loop", "-1", "-ss", at, "-i", music });
        }
        else if (music is not null)
        {
            args.AddRange(new[] { "-stream_loop", "-1", "-i", music });
        }
        else
        {
            args.AddRange(new[] { "-f", "lavfi", "-i", "anullsrc=r=48000:cl=stereo" });
        }

        const string Hold = "loop=loop=-1:size=1:start=0,setpts=N*1001/30000/TB";
        var speed = ScrollSpeed.ToString(Inv);
        var gh = assets.GridHeight.ToString(Inv);
        var v = new StringBuilder();
        v.Append(Inv, $"[0:v]{Hold}[gbg];[1:v]{Hold}[ggr];");
        v.Append(Inv, $"color=c={GridBg}:s={W}x{GridH}:r=30000/1001[ggc];");
        if (assets.GridHeight > GridH)
        {
            // Two copies stacked, sliding up together, so the list loops without a gap.
            v.Append("[ggr]split[gg1][gg2];");
            v.Append(Inv, $"[ggc][gg1]overlay=x=0:y='-mod(t*{speed}\\,{gh})':eval=frame[ggx];");
            v.Append(Inv, $"[ggx][gg2]overlay=x=0:y='{gh}-mod(t*{speed}\\,{gh})':eval=frame[ggs];");
        }
        else
        {
            v.Append("[ggc][ggr]overlay=0:0[ggs];");
        }

        v.Append(Inv, $"[gbg][ggs]overlay=0:{GridY}[gv0];");
        var last = "gv0";
        var k = assets.Promos.Count;
        for (var i = 0; i < k; i++)
        {
            var on = k > 1
                ? string.Format(Inv, ":enable='between(mod(t\\,{0})\\,{1})'", k * PromoSeconds, $"{i * PromoSeconds}\\,{(i * PromoSeconds) + PromoSeconds}")
                : string.Empty;
            v.Append(Inv, $"[{2 + i}:v]{Hold}[gp{i}];[{last}][gp{i}]overlay=20:20{on}[gv{i + 1}];");
            last = $"gv{i + 1}";
        }

        Directory.CreateDirectory(workDir);
        var clock = Path.Combine(workDir, "clock.txt");
        var ampm = Path.Combine(workDir, "ampm.txt");
        File.WriteAllText(clock, "%{pts:gmtime:" + localEpoch.ToString(Inv) + ":%l\\:%M}", new UTF8Encoding(false));
        File.WriteAllText(ampm, "%{pts:gmtime:" + localEpoch.ToString(Inv) + ":%p}", new UTF8Encoding(false));
        v.Append(Inv, $"[{last}]drawtext=fontfile={bold}:textfile={clock}:fontsize=96:fontcolor=white:x=1080-tw/2:y=140");
        v.Append(Inv, $",drawtext=fontfile={bold}:textfile={ampm}:fontsize=28:fontcolor=0xC8D2FF:x=1080-tw/2:y=244");
        // Paced to real time, except the first few seconds after tuning in, which go out in one quick burst
        // (their timestamps are held at 0 for the pacer, then put back).
        var b = burstSeconds.ToString("0.000", Inv);
        var pace = burstSeconds > 0.5 ? $"setpts='if(lt(T\\,{b})\\,0\\,PTS-{b}/TB)',realtime,setpts=N*1001/30000/TB" : "realtime";
        var apace = burstSeconds > 0.5 ? $"asetpts='if(lt(T\\,{b})\\,0\\,PTS-{b}/TB)',arealtime,asetpts=N/SR/TB" : "arealtime";
        v.Append(Inv, $",drawgrid=w=iw:h=3:t=1:c=black@0.16,{pace},format=yuv420p[bv];");

        var a = 2 + k;
        if (music is not null)
        {
            var vol = Math.Clamp(Config.GuideMusicVolumeDb, -40, 6).ToString(Inv);
            var fadeIn = builtIn ? "0.3" : "1.5";
            v.Append(Inv, $"[{a}:a]volume={vol}dB,aresample=48000,aformat=channel_layouts=stereo,afade=t=in:st=0:d={fadeIn},{apace}[ba]");
        }
        else
        {
            v.Append(Inv, $"[{a}:a]aformat=channel_layouts=stereo,{apace}[ba]");
        }

        args.AddRange(new[] { "-filter_complex", v.ToString() });
        return args;
    }

    /// <summary>A random track from the music folder; else the built-in synth loop (if turned on); else null = silent.</summary>
    private static string? PickMusic(out bool builtIn)
    {
        builtIn = false;
        var folder = Config.GuideMusicFolder;
        if (!string.IsNullOrWhiteSpace(folder))
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                        .Where(f => MusicExt.Contains(Path.GetExtension(f).ToLowerInvariant()))
                        .Take(2000)
                        .ToList();
                    if (files.Count > 0)
                    {
                        return files[Random.Shared.Next(files.Count)];
                    }
                }
            }
            catch (Exception)
            {
                // fall through to the built-in music
            }
        }

        if (!Config.GuideDefaultMusic)
        {
            return null;
        }

        var bundled = BuiltInMusic();
        builtIn = bundled is not null;
        return bundled;
    }

    /// <summary>The bundled synth loop, written next to the plugin settings once.</summary>
    private static string? BuiltInMusic()
    {
        lock (MusicGate)
        {
            try
            {
                var dir = Path.Combine(ChannelScheduler.BaseDir(), "guide");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, "guide-synth.opus");
                if (!File.Exists(file))
                {
                    var asm = typeof(Plugin).Assembly;
                    var res = asm.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith(".guide-synth.opus", StringComparison.OrdinalIgnoreCase));
                    if (res is null)
                    {
                        return null;
                    }

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

    private static string Title(BaseItem item)
    {
        if (item is Episode ep)
        {
            return ep.SeriesName ?? ep.Series?.Name ?? item.Name ?? string.Empty;
        }

        if (item is MusicVideo)
        {
            return ChannelRules.DisplayName(item);
        }

        return item.ProductionYear is int y ? $"{item.Name} ({y.ToString(Inv)})" : item.Name ?? string.Empty;
    }

    /// <summary>Shorten text to fit <paramref name="width"/> pixels (rough average glyph width), with "…".</summary>
    private static string Fit(string text, double width, int size, double perChar)
    {
        var max = (int)(width / (size * perChar));
        if (max < 3)
        {
            return string.Empty;
        }

        return text.Length <= max ? text : text[..Math.Max(1, max - 1)].TrimEnd() + "…";
    }

    private static string Box(int x, int y, int w, int h, string color, int thickness = 0)
        => string.Format(Inv, "drawbox=x={0}:y={1}:w={2}:h={3}:color={4}:t={5}", x, y, w, h, color, thickness > 0 ? thickness.ToString(Inv) : "fill");

    private static async Task<bool> Render(string ffmpeg, IEnumerable<string> args, ILogger logger, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error", "-nostdin", "-y" }.Concat(args))
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };
        proc.Start();
        var err = proc.StandardError.ReadToEndAsync(ct);
        _ = proc.StandardOutput.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                proc.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // already gone
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning("QuasiTV guide: ffmpeg took too long drawing the guide");
            return false;
        }

        if (proc.ExitCode != 0)
        {
            var text = await err.ConfigureAwait(false);
            logger.LogWarning("QuasiTV guide: ffmpeg could not draw the guide: {Error}", text.Length > 1500 ? text[^1500..] : text);
            return false;
        }

        return true;
    }

    /// <summary>Writes each piece of text to its own file (no ffmpeg escaping problems) and returns the drawtext filter.</summary>
    private sealed class Pen
    {
        private readonly string _dir;
        private readonly string _font;
        private int _n;

        public Pen(string dir, string font)
        {
            _dir = dir;
            _font = font;
        }

        public string Text(string text, int size, string color, string x, string y, string extra = "")
        {
            var file = Path.Combine(_dir, $"t{++_n}.txt");
            File.WriteAllText(file, text, new UTF8Encoding(false));
            return $"drawtext=fontfile={_font}:textfile={file}:expansion=none:fontsize={size.ToString(Inv)}:fontcolor={color}:x={x}:y={y}{extra}";
        }
    }
}
