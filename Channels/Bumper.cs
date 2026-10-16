using System.Globalization;
using System.Text;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>
/// "Up Next" bumper, rendered live by ffmpeg in the channel stream (no pre-rendering):
/// the coming show's backdrop (sharp center, blurred + darkened edges, slow zoom), a dark scrim on the left,
/// blue bar + "UP NEXT", title, episode line, "time · channel" line, soft synth stinger mixed low.
/// Channel number/name are read at render time, so renaming a channel never breaks bumpers.
/// </summary>
public static class Bumper
{
    private static readonly object Gate = new();
    private static readonly string[] SoundExt = { ".mp3", ".wav", ".m4a", ".aac", ".ogg", ".flac" };

    /// <summary>Folder for your own stingers (random pick per bumper); empty = bundled stinger.</summary>
    public static string SoundsDir => Path.Combine(ChannelScheduler.BaseDir(), "bumper-sounds");

    /// <summary>ffmpeg input + filter arguments (everything before the encoder options). Output labels: [bv] video, [ba] audio.</summary>
    public static List<string>? Build(QuasiChannel channel, Slot slot, TimeSpan offset, int height, string workDir, out TimeSpan length)
    {
        length = slot.Bumper - offset;
        var fonts = Banner.FontDir();
        if (fonts is null || length <= TimeSpan.Zero)
        {
            return null;
        }

        var inv = CultureInfo.InvariantCulture;
        var cfg = Plugin.Instance!.Configuration;
        var h = height;
        var w = (int)Math.Round(h * 16 / 9.0 / 2) * 2;
        var d = slot.Bumper.TotalSeconds;
        var ds = d.ToString("0.000", inv);
        var fps = "30000/1001";

        // The background is composed ONCE (blur, vignette, scrim on a single frame at 1.5x size),
        // then zoompan makes the frames from it — ~3x cheaper than filtering every frame.
        var cw = w * 3 / 2;
        var ch = h * 3 / 2;
        var assets = Assets(cw, ch);
        if (assets is null)
        {
            return null;
        }

        // ---- text
        var item = slot.Item;
        string title, detail;
        if (item is Episode ep)
        {
            title = ep.SeriesName ?? ep.Series?.Name ?? item.Name ?? string.Empty;
            var num = ep.IndexNumber is int e ? $"S{ep.ParentIndexNumber ?? 1} E{e}" : string.Empty;
            detail = string.Join("  ·  ", new[] { num, ep.Name ?? string.Empty }.Where(x => x.Length > 0));
        }
        else
        {
            title = item.Name ?? string.Empty;
            detail = string.Join("  ·  ", new[] { item.ProductionYear?.ToString(inv) ?? string.Empty, item.OfficialRating ?? string.Empty }.Where(x => x.Length > 0));
        }

        var tz = Banner.ResolveTimeZone(cfg.TimeZoneId);
        var at = TimeZoneInfo.ConvertTimeFromUtc(slot.ItemStartUtc, tz).ToString("h:mm tt", CultureInfo.GetCultureInfo("en-US"));
        var when = $"{at}   ·   {channel.Number}  {channel.Name}";

        Directory.CreateDirectory(workDir);
        string Txt(string name, string text)
        {
            var f = Path.Combine(workDir, name);
            File.WriteAllText(f, text, new UTF8Encoding(false));
            return f;
        }

        if (title.Length > 48)
        {
            title = title[..46] + "…";
        }

        var fUp = Txt("b0.txt", "UP NEXT");
        var fTitle = Txt("b1.txt", title);
        var fDetail = Txt("b2.txt", detail.Length > 60 ? detail[..58] + "…" : detail);
        var fWhen = Txt("b3.txt", when);

        // Title size: big, but shrink long titles so they fit in ~78% of the width.
        var titleSize = Math.Min(h * 0.085, w * 0.78 / Math.Max(1, title.Length * 0.64));
        var bold = Path.Combine(fonts, "DejaVuSans-Bold.ttf");
        var regular = Path.Combine(fonts, "DejaVuSans.ttf");

        // ---- inputs
        var args = new List<string>();
        var image = Backdrop(item);
        if (image is not null)
        {
            args.AddRange(new[] { "-i", image });
        }
        else
        {
            // No art: Jellyfin-colored gradient (navy → purple → blue).
            args.AddRange(new[] { "-f", "lavfi", "-i", $"gradients=s={cw}x{ch}:c0=0x000B25:c1=0x000B25:c2=0x7A4A9E:c3=0x0088C0:x0=0:y0={ch}:x1={cw}:y1=0:nb_colors=4:speed=0.00001:rate=1:duration=1" });
        }

        args.AddRange(new[] { "-i", assets.Value.Mask });
        args.AddRange(new[] { "-i", assets.Value.Scrim });

        var (sound, custom) = PickSound(assets.Value.Stinger);
        if (sound is not null)
        {
            args.AddRange(new[] { "-i", sound });
        }
        else
        {
            args.AddRange(new[] { "-f", "lavfi", "-t", ds, "-i", "anullsrc=r=48000:cl=stereo" });
        }

        // ---- video
        string Fade(double from, double dur) => $"min(max((t-{from.ToString("0.00", inv)})/{dur.ToString("0.00", inv)}\\,0)\\,1)";
        var fadeOutAt = Math.Max(0.5, d - 0.6).ToString("0.00", inv);
        var frames = (int)Math.Ceiling(d * 30000 / 1001.0);
        var v = new StringBuilder();
        v.Append(CultureInfo.InvariantCulture, $"[0:v]trim=end_frame=1,scale={cw}:{ch}:force_original_aspect_ratio=increase,crop={cw}:{ch},setsar=1,format=yuv420p,split[za][zb];");
        v.Append(CultureInfo.InvariantCulture, $"[za]scale={cw / 2 / 2 * 2}:{ch / 2 / 2 * 2},gblur=sigma=9,eq=brightness=-0.06:saturation=0.85,scale={cw}:{ch}[zbl];");
        v.Append("[1:v]format=gray[zm];[zb]format=yuva420p[zs0];[zs0][zm]alphamerge[zs];");
        v.Append("[zbl][zs]overlay=format=auto,vignette=angle=PI/4.5[zbg];");
        v.Append(CultureInfo.InvariantCulture, $"color=c=black:s={cw}x{ch}:d=1,format=yuva420p[zk];[2:v]format=gray[zsm];[zk][zsm]alphamerge[zscr];");
        v.Append("[zbg][zscr]overlay=format=auto:shortest=1,");
        v.Append(CultureInfo.InvariantCulture, $"zoompan=z='1.0+0.06*on/{frames}':x='iw/2-(iw/zoom/2)':y='ih/2-(ih/zoom/2)':d={frames}:s={w}x{h}:fps={fps}");
        v.Append(CultureInfo.InvariantCulture, $",drawbox=x=iw*0.07:y=ih*0.36:w=max(3\\,ih*0.0055):h=ih*0.30:color=0x00A4DC@1:t=fill:enable='gte(t\\,0.3)'");
        v.Append(CultureInfo.InvariantCulture, $",drawtext=fontfile={bold}:textfile={fUp}:expansion=none:fontcolor=0x00A4DC:fontsize=h*0.035:x=w*0.09:y=h*0.37:alpha='{Fade(0.3, 0.4)}'");
        v.Append(CultureInfo.InvariantCulture, $",drawtext=fontfile={bold}:textfile={fTitle}:expansion=none:fontcolor=white:fontsize={titleSize.ToString("0", inv)}:x=w*0.09-20*max(0\\,1-(t-0.5)/0.5):y=h*0.43:alpha='{Fade(0.5, 0.5)}':shadowcolor=black@0.6:shadowx=2:shadowy=2");
        if (detail.Length > 0)
        {
            v.Append(CultureInfo.InvariantCulture, $",drawtext=fontfile={regular}:textfile={fDetail}:expansion=none:fontcolor=white@0.9:fontsize=h*0.040:x=w*0.09:y=h*0.55:alpha='{Fade(0.8, 0.5)}':shadowcolor=black@0.6:shadowx=1:shadowy=1");
        }

        v.Append(CultureInfo.InvariantCulture, $",drawtext=fontfile={regular}:textfile={fWhen}:expansion=none:fontcolor=white@0.75:fontsize=h*0.030:x=w*0.09:y=h*{(detail.Length > 0 ? "0.62" : "0.55")}:alpha='{Fade(1.0, 0.5)}':shadowcolor=black@0.5:shadowx=1:shadowy=1");
        v.Append(CultureInfo.InvariantCulture, $",fade=t=in:st=0:d=0.5,fade=t=out:st={fadeOutAt}:d=0.6");
        v.Append(CultureInfo.InvariantCulture, $",trim=start={offset.TotalSeconds.ToString("0.000", inv)},setpts=PTS-STARTPTS,realtime,format=yuv420p[bv];");

        // ---- audio: stinger mixed low, starts 0.3 s in, fades with the picture
        var level = custom ? "loudnorm=I=-16:TP=-2," : string.Empty;
        var vol = Math.Clamp(cfg.BumperVolumeDb, -40, 6).ToString(inv);
        var aFadeAt = Math.Max(0.3, d - 0.8).ToString("0.00", inv);
        v.Append(CultureInfo.InvariantCulture, $"[3:a]{level}volume={vol}dB,aresample=48000,aformat=channel_layouts=stereo,adelay=300|300,afade=t=out:st={aFadeAt}:d=0.8,apad,atrim=0:{ds},");
        v.Append(CultureInfo.InvariantCulture, $"atrim=start={offset.TotalSeconds.ToString("0.000", inv)},asetpts=PTS-STARTPTS,arealtime[ba]");

        args.AddRange(new[] { "-filter_complex", v.ToString() });
        return args;
    }

    internal static string? Backdrop(BaseItem item)
    {
        var owner = item is Episode ep && ep.Series is not null ? (BaseItem)ep.Series : item;
        foreach (var candidate in new[] { owner, item })
        {
            foreach (var type in new[] { ImageType.Backdrop, ImageType.Thumb })
            {
                try
                {
                    if (candidate.HasImage(type, 0))
                    {
                        var p = candidate.GetImagePath(type, 0);
                        if (!string.IsNullOrEmpty(p) && File.Exists(p))
                        {
                            return p;
                        }
                    }
                }
                catch (Exception)
                {
                    // try the next one
                }
            }
        }

        return null;
    }

    private static (string? Path, bool Custom) PickSound(string? bundled)
    {
        try
        {
            if (Directory.Exists(SoundsDir))
            {
                var files = Directory.GetFiles(SoundsDir).Where(f => SoundExt.Contains(Path.GetExtension(f).ToLowerInvariant())).ToArray();
                if (files.Length > 0)
                {
                    return (files[Random.Shared.Next(files.Length)], true);
                }
            }
        }
        catch (Exception)
        {
            // fall back to the bundled stinger
        }

        return (bundled, false);
    }

    /// <summary>Bundled stinger + vignette mask + left scrim (written once per size, next to the plugin settings).</summary>
    private static (string Mask, string Scrim, string? Stinger)? Assets(int w, int h)
    {
        lock (Gate)
        {
            try
            {
                var dir = Path.Combine(ChannelScheduler.BaseDir(), "bumper");
                Directory.CreateDirectory(dir);

                var stinger = Path.Combine(dir, "stinger.mp3");
                if (!File.Exists(stinger))
                {
                    var asm = typeof(Plugin).Assembly;
                    var res = asm.GetManifestResourceNames().FirstOrDefault(r => r.EndsWith(".stinger.mp3", StringComparison.OrdinalIgnoreCase));
                    if (res is not null)
                    {
                        using var src = asm.GetManifestResourceStream(res)!;
                        using var dst = File.Create(stinger);
                        src.CopyTo(dst);
                    }
                }

                // Sharp in the middle (a bit right of center, where the art's subject usually is), blurred towards the edges.
                var mask = Path.Combine(dir, $"mask-{w}x{h}.pgm");
                if (!File.Exists(mask))
                {
                    WritePgm(mask, w, h, (x, y) =>
                    {
                        var dx = (x - w * 0.62) / (w * 0.42);
                        var dy = (y - h * 0.48) / (h * 0.55);
                        var r = Math.Sqrt(dx * dx + dy * dy);
                        return Math.Clamp(1 - (r - 0.45) / 0.55, 0, 1);
                    });
                }

                // Darkens the left side so the text reads on bright art.
                var scrim = Path.Combine(dir, $"scrim-{w}x{h}.pgm");
                if (!File.Exists(scrim))
                {
                    WritePgm(scrim, w, h, (x, _) => Math.Clamp(0.92 - (x / (double)w) * 1.15, 0, 1));
                }

                return (mask, scrim, File.Exists(stinger) ? stinger : null);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    private static void WritePgm(string path, int w, int h, Func<int, int, double> value)
    {
        var header = Encoding.ASCII.GetBytes($"P5\n{w} {h}\n255\n");
        var data = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                data[(y * w) + x] = (byte)Math.Round(value(x, y) * 255);
            }
        }

        var tmp = path + ".tmp";
        using (var f = File.Create(tmp))
        {
            f.Write(header);
            f.Write(data);
        }

        File.Move(tmp, path, true);
    }
}
