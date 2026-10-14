using System.Globalization;
using MediaBrowser.Controller.Entities;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>
/// MTV-style music video caption burned into the lower left: artist, "song", year.
/// Shown a few seconds after a video starts and again near its end; if you tune in
/// partway through, it shows right away so you know what's playing.
/// </summary>
public static class Caption
{
    private const double ShowFor = 8;

    /// <summary>Extra filter chain (starting with ",") or "" when there's nothing to show.</summary>
    public static string Filter(BaseItem item, TimeSpan itemOffset, TimeSpan play, string workDir)
    {
        var fonts = Banner.FontDir();
        if (fonts is null)
        {
            return string.Empty;
        }

        var artist = ChannelRules.Artist(item);
        var song = item.Name ?? string.Empty;
        if (artist.Length == 0 && song.Length == 0)
        {
            return string.Empty;
        }

        // Windows in seconds of the video itself.
        var off = itemOffset.TotalSeconds;
        var len = play.TotalSeconds;
        var windows = new List<(double From, double To)>();
        if (off < 12)
        {
            windows.Add((4, 4 + ShowFor));
        }
        else if (off < len - 25)
        {
            windows.Add((off + 1, off + 1 + ShowFor)); // tuned in partway: say what this is
        }

        if (len > 45 && off < len - 8)
        {
            windows.Add((len - 16, len - 16 + ShowFor));
        }

        // ffmpeg's t starts at 0 where this run starts in the video.
        var inv = CultureInfo.InvariantCulture;
        var parts = windows
            .Select(w => (From: Math.Max(0, w.From - off), To: w.To - off))
            .Where(w => w.To > 0.5)
            .Select(w => string.Format(inv, "between(t,{0:0.00},{1:0.00})", w.From, w.To))
            .ToList();
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        var on = ":enable='" + string.Join("+", parts) + "'";

        Directory.CreateDirectory(workDir);
        var lines = new List<(string File, string Text, bool Bold, double Size)>();
        if (artist.Length > 0)
        {
            lines.Add((Path.Combine(workDir, "c1.txt"), Trim(artist), true, 0.044));
        }

        if (song.Length > 0)
        {
            lines.Add((Path.Combine(workDir, "c2.txt"), Trim("“" + song + "”"), false, 0.040));
        }

        if (item.ProductionYear is int y && y > 0)
        {
            lines.Add((Path.Combine(workDir, "c3.txt"), y.ToString(inv), false, 0.032));
        }

        var bold = Path.Combine(fonts, "DejaVuSans-Bold.ttf");
        var regular = Path.Combine(fonts, "DejaVuSans.ttf");
        var filter = string.Empty;
        var top = 0.70;
        foreach (var (file, text, isBold, size) in lines)
        {
            File.WriteAllText(file, text);
            filter += string.Format(
                inv,
                ",drawtext=fontfile={0}:textfile={1}:expansion=none:fontcolor=white:fontsize=h*{2:0.000}:borderw=2:bordercolor=black@0.65:shadowx=2:shadowy=2:shadowcolor=black@0.5:x=w*0.05:y=h*{3:0.000}{4}",
                isBold ? bold : regular,
                file,
                size,
                top,
                on);
            top += size + 0.018;
        }

        return filter;
    }

    private static string Trim(string s) => s.Length > 48 ? s[..45] + "…" : s;
}
