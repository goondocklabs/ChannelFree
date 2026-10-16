using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Xml;
using Jellyfin.Plugin.QuasiTV.Channels;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QuasiTV.Api;

/// <summary>
/// Endpoints (all require ?token=, see plugin settings page):
///   GET  /QuasiTV/channels.m3u              M3U tuner for Jellyfin Live TV
///   GET  /QuasiTV/guide.xml                 XMLTV guide
///   GET  /QuasiTV/stream/{id}               MPEG-TS, started on demand, stops when nobody's watching
///   GET  /QuasiTV/status                    now/next per channel (JSON)
///   POST /QuasiTV/control/{id}/{action}     action = restart | skip
/// </summary>
[ApiController]
[Route("QuasiTV")]
[AllowAnonymous]
public class QuasiTvController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly IMediaEncoder _mediaEncoder;
    private readonly ILogger<QuasiTvController> _logger;
    private readonly IServerApplicationHost _host;
    private readonly IHttpClientFactory _http;

    public QuasiTvController(ILibraryManager library, IMediaEncoder mediaEncoder, ILogger<QuasiTvController> logger, IServerApplicationHost host, IHttpClientFactory http)
    {
        _library = library;
        _mediaEncoder = mediaEncoder;
        _logger = logger;
        _host = host;
        _http = http;
    }

    /// <summary>Seconds of video sent in a quick burst when someone tunes in, so the player can start right away.</summary>
    private const double TuneInBurstSeconds = 15;

    private static Configuration.PluginConfiguration Config => Plugin.Instance!.Configuration;

    private static bool TokenOk(string? token)
        => !string.IsNullOrEmpty(token) && string.Equals(token, Config.StreamToken, StringComparison.Ordinal);

    // ---------------------------------------------------------------- M3U + guide

    [HttpGet("channels.m3u")]
    public IActionResult Playlist([FromQuery] string? token)
    {
        if (!TokenOk(token))
        {
            return Unauthorized();
        }

        var baseUrl = Config.InternalUrl.TrimEnd('/');
        var sb = new StringBuilder("#EXTM3U\n");
        if (GuideChannel.Enabled)
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"#EXTINF:-1 tvg-id=\"{GuideChannel.Id}\" tvg-chno=\"{GuideChannel.Number}\" tvg-name=\"{GuideChannel.Name}\" group-title=\"Channel Free\",{GuideChannel.Name}\n");
            sb.Append(CultureInfo.InvariantCulture, $"{baseUrl}/QuasiTV/stream/{GuideChannel.Id}?token={Config.StreamToken}\n");
        }

        foreach (var ch in ChannelScheduler.GetChannels(_library))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"#EXTINF:-1 tvg-id=\"{ch.Id}\" tvg-chno=\"{ch.Number}\" tvg-name=\"{ch.Name}\" group-title=\"Channel Free\",{ch.Name}\n");
            sb.Append(CultureInfo.InvariantCulture, $"{baseUrl}/QuasiTV/stream/{ch.Id}?token={Config.StreamToken}\n");
        }

        return Content(sb.ToString(), "audio/x-mpegurl", Encoding.UTF8);
    }

    [HttpGet("guide.xml")]
    public IActionResult Guide([FromQuery] string? token)
    {
        if (!TokenOk(token))
        {
            return Unauthorized();
        }

        var now = DateTime.UtcNow;
        var from = now.AddHours(-2);
        var until = now.AddHours(48);
        var baseUrl = Config.InternalUrl.TrimEnd('/');

        using var ms = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = true };
        using (var w = XmlWriter.Create(ms, settings))
        {
            w.WriteStartDocument();
            w.WriteStartElement("tv");
            w.WriteAttributeString("generator-info-name", "Channel Free");

            var channels = ChannelScheduler.GetChannels(_library);
            if (GuideChannel.Enabled)
            {
                w.WriteStartElement("channel");
                w.WriteAttributeString("id", GuideChannel.Id);
                w.WriteElementString("display-name", GuideChannel.Name);
                w.WriteElementString("display-name", GuideChannel.Number.ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();

                // Half hour blocks, like the real thing.
                var block = new DateTime(from.Ticks - (from.Ticks % TimeSpan.FromMinutes(30).Ticks), DateTimeKind.Utc);
                while (block < until)
                {
                    w.WriteStartElement("programme");
                    w.WriteAttributeString("start", XmlTvTime(block));
                    w.WriteAttributeString("stop", XmlTvTime(block.AddMinutes(30)));
                    w.WriteAttributeString("channel", GuideChannel.Id);
                    w.WriteElementString("title", "Channel Guide");
                    w.WriteElementString("desc", "What's on every channel right now and coming up next.");
                    w.WriteEndElement();
                    block = block.AddMinutes(30);
                }
            }

            foreach (var ch in channels)
            {
                w.WriteStartElement("channel");
                w.WriteAttributeString("id", ch.Id);
                w.WriteElementString("display-name", ch.Name);
                w.WriteElementString("display-name", ch.Number.ToString(CultureInfo.InvariantCulture));
                w.WriteEndElement();
            }

            foreach (var ch in channels)
            {
                foreach (var slot in ChannelScheduler.Slots(ch, from, until))
                {
                    var item = slot.Item;
                    var ep = item as Episode;
                    var show = ep?.Series;
                    w.WriteStartElement("programme");
                    w.WriteAttributeString("start", XmlTvTime(slot.StartUtc));
                    w.WriteAttributeString("stop", XmlTvTime(slot.EndUtc));
                    w.WriteAttributeString("channel", ch.Id);
                    w.WriteElementString("title", ep is not null ? (ep.SeriesName ?? show?.Name ?? item.Name ?? "Untitled") : ChannelRules.DisplayName(item));
                    if (ep is not null && !string.IsNullOrWhiteSpace(ep.Name))
                    {
                        w.WriteElementString("sub-title", ep.Name);
                    }

                    var desc = !string.IsNullOrWhiteSpace(item.Overview) ? item.Overview : show?.Overview;
                    if (!string.IsNullOrWhiteSpace(desc))
                    {
                        w.WriteElementString("desc", desc);
                    }

                    var year = ep is not null ? show?.ProductionYear : item.ProductionYear;
                    if (year is int y)
                    {
                        w.WriteElementString("date", y.ToString(CultureInfo.InvariantCulture));
                    }

                    w.WriteElementString("category", ep is not null ? "Series" : item is MusicVideo ? "Music" : "Movie");

                    // Wide artwork for the guide: show art for episodes, movie art for movies.
                    BaseItem artItem = show is not null && WideArt(show) is not null ? show : item;
                    var art = WideArt(artItem);
                    if (art is not null)
                    {
                        w.WriteStartElement("icon");
                        w.WriteAttributeString("src", $"{baseUrl}/Items/{artItem.Id:N}/Images/{art}");
                        w.WriteEndElement();
                    }

                    if (ep is not null && ep.IndexNumber is int epNum)
                    {
                        var season = ep.ParentIndexNumber ?? 1;
                        w.WriteStartElement("episode-num");
                        w.WriteAttributeString("system", "xmltv_ns");
                        w.WriteString(string.Format(CultureInfo.InvariantCulture, "{0}.{1}.", Math.Max(0, season - 1), Math.Max(0, epNum - 1)));
                        w.WriteEndElement();
                        w.WriteStartElement("episode-num");
                        w.WriteAttributeString("system", "onscreen");
                        w.WriteString(string.Format(CultureInfo.InvariantCulture, "S{0:00}E{1:00}", season, epNum));
                        w.WriteEndElement();
                        w.WriteElementString("previously-shown", string.Empty);
                    }

                    var rating = ep is not null ? (show?.OfficialRating ?? item.OfficialRating) : item.OfficialRating;
                    if (!string.IsNullOrWhiteSpace(rating))
                    {
                        w.WriteStartElement("rating");
                        w.WriteElementString("value", rating);
                        w.WriteEndElement();
                    }

                    w.WriteEndElement();
                }
            }

            w.WriteEndElement();
            w.WriteEndDocument();
        }

        return File(ms.ToArray(), "application/xml");
    }

    // ---------------------------------------------------------------- remote control

    [HttpGet("status")]
    public IActionResult Status([FromQuery] string? token)
    {
        if (!TokenOk(token))
        {
            return Unauthorized();
        }

        var now = DateTime.UtcNow;
        var list = ChannelScheduler.GetChannels(_library).Select(ch =>
        {
            var slots = ChannelScheduler.Slots(ch, now, now.AddHours(6)).Take(2).ToList();
            var cur = slots.ElementAtOrDefault(0);
            var next = slots.ElementAtOrDefault(1);
            return new
            {
                id = ch.Id,
                name = ch.Name,
                number = ch.Number,
                now = cur?.Item.Name,
                nowYear = cur?.Item.ProductionYear,
                nowStart = cur?.StartUtc,
                nowEnd = cur?.EndUtc,
                next = next?.Item.Name
            };
        });

        return Ok(new { serverTimeUtc = now, channels = list });
    }

    [HttpPost("control/{channelId}/{action}")]
    public IActionResult Control([FromRoute] string channelId, [FromRoute] string action, [FromQuery] string? token)
    {
        if (!TokenOk(token))
        {
            return Unauthorized();
        }

        var channel = ChannelScheduler.Find(_library, channelId);
        if (channel is null)
        {
            return NotFound();
        }

        var act = action.ToLowerInvariant();
        if ((act == "restart" && !Config.AllowRestart) || (act == "skip" && !Config.AllowJumpAhead))
        {
            return StatusCode(403, act + " is turned off in Channel Free settings");
        }

        switch (act)
        {
            case "restart":
                JumpManager.Clear(channel.Id);
                ChannelScheduler.Restart(channel);
                break;
            case "skip":
                JumpManager.Clear(channel.Id);
                ChannelScheduler.Skip(channel);
                break;
            default:
                return BadRequest("action must be restart or skip");
        }

        var live = StreamRegistry.Nudge(channel.Id);
        GuideRefresher.Request();
        var cur = ChannelScheduler.Current(channel, DateTime.UtcNow);
        _logger.LogInformation("QuasiTV: {Action} on {Channel} -> {Title} (live stream switched: {Live})", action, channel.Name, cur?.Item.Name, live);
        return Ok(new { ok = true, now = cur?.Item.Name, liveSwitched = live });
    }

    // ---------------------------------------------------------------- streaming

    [HttpGet("stream/{channelId}")]
    public async Task Stream([FromRoute] string channelId, [FromQuery] string? token)
    {
        if (!TokenOk(token))
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        if (string.Equals(channelId, GuideChannel.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (!GuideChannel.Enabled)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await StreamGuide().ConfigureAwait(false);
            return;
        }

        var channel = ChannelScheduler.Find(_library, channelId);
        if (channel is null)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Backed out soon after a Record-jump and now returning? Put the channel back first.
        if (JumpManager.OnTuneIn(channel))
        {
            _logger.LogInformation("QuasiTV: returned to {Channel} after backing out of a jump; reverted to the previous program", channel.Name);
            GuideRefresher.Request(waitUntilIdle: true); // someone is tuning in right now
        }

        // Newest tune-in wins: stops any older (possibly abandoned) encoder on this channel.
        var active = StreamRegistry.Take(channel.Id);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, active.Replaced);
        var ct = session.Token;

        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "video/mp2t";
        await Response.StartAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("QuasiTV: tuned in to {Channel}", channel.Name);
        var tunedAt = DateTime.UtcNow;
        var showBanner = true; // cable-box info banner on the first program after tuning in
        var workDir = Path.Combine(Path.GetTempPath(), "cablepackage", Guid.NewGuid().ToString("N"));
        double streamSeconds = 0; // running output timestamp, so items join into one continuous stream
        var quickExits = 0;
        var useHw = Config.UseHardwareEncoding && System.IO.File.Exists(Config.VaapiDevice);
        var bumperBroken = false;
        var captionBroken = false;
        var popupBroken = false;
        var firstRun = true; // the first program after tuning in starts a little early and is sent in a quick burst

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                var slot = ChannelScheduler.Current(channel, now);
                if (slot is null)
                {
                    // Gap (e.g. an item was deleted from the library): wait for the next program.
                    if (++quickExits >= 60)
                    {
                        break;
                    }

                    await Task.Delay(2000, ct).ConfigureAwait(false);
                    continue;
                }

                var offset = now - slot.StartUtc;
                var inBumper = offset < slot.Bumper;
                var itemOffset = inBumper ? TimeSpan.Zero : offset - slot.Bumper;
                var remaining = inBumper ? slot.Bumper - offset : slot.Play - itemOffset;
                if (remaining < TimeSpan.FromMilliseconds(300))
                {
                    await Task.Delay(remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
                    continue;
                }

                // Tuning in: players wait for several seconds of video before showing anything, and a live channel
                // only makes video in real time. So start a few seconds back and send those seconds in one quick burst.
                var lead = TimeSpan.Zero;
                if (firstRun && !inBumper)
                {
                    lead = TimeSpan.FromSeconds(Math.Min(TuneInBurstSeconds, itemOffset.TotalSeconds));
                    itemOffset -= lead;
                    remaining += lead;
                }

                firstRun = false;

                using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
                active.CurrentRun = run;
                var switched = false;
                var started = DateTime.UtcNow;
                var banner = string.Empty;
                var bumperRun = false;
                List<double>? pops = null;

                try
                {
                    if (inBumper && !bumperBroken)
                    {
                        showBanner = false; // the bumper already says what's on
                        var bumperArgs = Bumper.Build(channel, slot, offset, Math.Clamp(Config.MaxHeight, 360, 1080), Path.Combine(workDir, "bumper"), out _);
                        if (bumperArgs is not null)
                        {
                            bumperRun = true;
                            _logger.LogInformation("QuasiTV: {Channel} bumper -> {Title}", channel.Name, slot.Item.Name);
                            await RunBumper(bumperArgs, streamSeconds, useHw, run.Token).ConfigureAwait(false);
                        }
                    }

                    if (!bumperRun)
                    {
                        if (inBumper)
                        {
                            // Bumper unavailable: hold on the previous frame until the program starts.
                            await Task.Delay(remaining, run.Token).ConfigureAwait(false);
                        }
                        else
                        {
                            if (slot.Item is MusicVideo && Config.MusicVideoCaptions && !captionBroken)
                            {
                                showBanner = false; // the caption says what's on
                                try
                                {
                                    banner = Caption.Filter(slot.Item, itemOffset, slot.Play, workDir);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "QuasiTV: music video caption failed; playing without it");
                                }
                            }
                            else if (showBanner)
                            {
                                showBanner = false;
                                try
                                {
                                    banner = Banner.Filter(channel, slot, now, workDir);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogWarning(ex, "QuasiTV: banner failed; playing without it");
                                }
                            }

                            if (slot.Item is MusicVideo && Config.PopupVideo && !popupBroken)
                            {
                                try
                                {
                                    var facts = await PopupVideo.FactsAsync(slot.Item, _http, _logger, TimeSpan.FromSeconds(2.5), run.Token).ConfigureAwait(false);
                                    var (popFilter, popTimes) = PopupVideo.Build(facts, itemOffset, slot.Play, Path.Combine(workDir, "popup"));
                                    banner += popFilter;
                                    pops = Config.PopupVideoSound && popTimes.Count > 0 ? popTimes : null;

                                    // Look up the next few videos while this one plays.
                                    var soon = ChannelScheduler.Slots(channel, now, now.AddMinutes(20)).Select(s => s.Item)
                                        .Where(i => i is MusicVideo && i.Id != slot.Item.Id).Take(4).ToList();
                                    PopupVideo.Prefetch(soon, _http, _logger);
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException)
                                {
                                    _logger.LogWarning(ex, "QuasiTV: Pop-Up Video bubbles failed; playing without them");
                                }
                            }

                            _logger.LogInformation("QuasiTV: {Channel} playing {Title} from {Offset:hh\\:mm\\:ss}{Trim} ({Encoder})", channel.Name, slot.Item.Name, itemOffset, slot.Trimmed ? $", credits cut at {slot.Play:hh\\:mm\\:ss}" : string.Empty, useHw ? "vaapi" : "x264");
                            await RunFfmpeg(slot.Item.Path, AudioMap(slot.Item), itemOffset, slot.Trimmed ? remaining : null, streamSeconds, useHw, banner, lead.TotalSeconds, pops, run.Token).ConfigureAwait(false);
                        }
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    switched = true; // Restart / Skip
                }
                finally
                {
                    active.CurrentRun = null;
                }

                var ran = DateTime.UtcNow - started;
                streamSeconds += ran.TotalSeconds + (ran > lead ? lead.TotalSeconds : 0);

                if (switched)
                {
                    quickExits = 0;
                    continue;
                }

                // ffmpeg quit almost immediately: GPU encode problem, file shorter than its metadata, or unreadable.
                var expectedMin = TimeSpan.FromSeconds(Math.Min(3, remaining.TotalSeconds * 0.5));
                if (ran < expectedMin)
                {
                    if (bumperRun)
                    {
                        _logger.LogWarning("QuasiTV: bumper failed on {Channel}; skipping bumpers for this stream", channel.Name);
                        bumperBroken = true;
                        continue;
                    }

                    if (banner.Length > 0)
                    {
                        captionBroken |= slot.Item is MusicVideo;
                        popupBroken |= slot.Item is MusicVideo;
                        _logger.LogWarning("QuasiTV: stream with info banner failed on {Title}; retrying without the banner", slot.Item.Name);
                        continue; // showBanner is already false
                    }

                    if (useHw)
                    {
                        _logger.LogWarning("QuasiTV: hardware encode failed on {Title}; falling back to software for this stream", slot.Item.Name);
                        useHw = false;
                        continue;
                    }

                    if (++quickExits >= 30)
                    {
                        _logger.LogWarning("QuasiTV: {Channel} keeps failing on {Title}; stopping stream", channel.Name, slot.Item.Name);
                        break;
                    }

                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                else
                {
                    quickExits = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // viewer left, or a newer tune-in replaced this stream
        }
        catch (IOException)
        {
            // viewer left mid-write
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, true);
                }
            }
            catch (IOException)
            {
                // temp files; not important
            }

            var replaced = active.Replaced.IsCancellationRequested;
            StreamRegistry.Release(channel.Id, active);
            active.Dispose();

            // Viewer left (not replaced by a newer tune-in): remember it; a later return may revert a recent jump.
            if (!replaced)
            {
                JumpManager.OnViewerLeft(channel, DateTime.UtcNow - tunedAt);
            }
        }

        _logger.LogInformation("QuasiTV: stream for {Channel} ended", channel.Name);
    }

    /// <summary>The guide channel: draw the stills for a run, play them (scrolling grid, rotating "Up next", live clock)
    /// until the next half hour (or 10 minutes), while the next run's stills are drawn in the background.</summary>
    private async Task StreamGuide()
    {
        var active = StreamRegistry.Take(GuideChannel.Id);
        using var session = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted, active.Replaced);
        var ct = session.Token;

        HttpContext.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
        Response.StatusCode = StatusCodes.Status200OK;
        Response.ContentType = "video/mp2t";
        await Response.StartAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("QuasiTV: tuned in to the guide channel");

        var workDir = Path.Combine(Path.GetTempPath(), "cablepackage", "guide-" + Guid.NewGuid().ToString("N"));
        var title = string.IsNullOrWhiteSpace(Config.GuideTitle) ? (_host.FriendlyName ?? "Channel Free") : Config.GuideTitle.Trim();
        var useHw = Config.UseHardwareEncoding && System.IO.File.Exists(Config.VaapiDevice);
        double streamSeconds = 0;
        var failures = 0;
        var turn = 0;
        double lag = 0; // seconds the viewer runs behind after the tune-in burst
        Task<GuideAssets?>? next = null;

        Task<GuideAssets?> Build(DateTime atUtc)
        {
            var dir = Path.Combine(workDir, (turn++ % 2 == 0) ? "a" : "b");
            return GuideChannel.BuildAsync(_library, _mediaEncoder.EncoderPath, atUtc, title, dir, _logger, ct);
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                GuideAssets? assets;
                try
                {
                    assets = await (next ?? Build(now)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "QuasiTV: drawing the guide failed");
                    assets = null;
                }

                next = null;
                if (assets is null)
                {
                    if (++failures >= 5)
                    {
                        _logger.LogWarning("QuasiTV: the guide channel keeps failing to draw; stopping its stream");
                        break;
                    }

                    await Task.Delay(3000, ct).ConfigureAwait(false);
                    continue;
                }

                now = DateTime.UtcNow;
                var end = GuideChannel.RunEnd(now);
                next = Build(end); // drawn while this run plays

                var started = DateTime.UtcNow;
                var burst = streamSeconds == 0 ? TuneInBurstSeconds : 0;
                var args = HwInit(useHw);
                var clockAt = started.AddSeconds(lag);
                var graph = GuideChannel.LiveArgs(assets, Path.Combine(workDir, "live" + (turn % 2).ToString(CultureInfo.InvariantCulture)), GuideChannel.LocalEpoch(clockAt), burst, out _);
                if (useHw)
                {
                    var i = graph.IndexOf("-filter_complex");
                    graph[i + 1] = graph[i + 1].Replace("format=yuv420p[bv]", "format=nv12,hwupload[bv]", StringComparison.Ordinal);
                }

                args.AddRange(graph);
                args.AddRange(new[] { "-map", "[bv]", "-map", "[ba]", "-t", ((end - started).TotalSeconds + burst).ToString("0.000", CultureInfo.InvariantCulture) });
                args.AddRange(EncoderArgs(useHw, streamSeconds));
                await Pump(args, ct).ConfigureAwait(false);

                var ran = DateTime.UtcNow - started;
                streamSeconds += ran.TotalSeconds + burst;
                lag += burst;
                if (ran < TimeSpan.FromSeconds(3))
                {
                    if (useHw)
                    {
                        _logger.LogWarning("QuasiTV: hardware encode failed on the guide channel; using software");
                        useHw = false;
                    }
                    else if (++failures >= 5)
                    {
                        _logger.LogWarning("QuasiTV: the guide channel keeps failing; stopping its stream");
                        break;
                    }

                    await Task.Delay(1000, ct).ConfigureAwait(false);
                }
                else
                {
                    failures = 0;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // viewer left, or a newer tune-in replaced this stream
        }
        catch (IOException)
        {
            // viewer left mid-write
        }
        finally
        {
            if (next is not null)
            {
                try
                {
                    await next.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // cancelled with the stream
                }
            }

            try
            {
                if (Directory.Exists(workDir))
                {
                    Directory.Delete(workDir, true);
                }
            }
            catch (IOException)
            {
                // temp files; not important
            }

            StreamRegistry.Release(GuideChannel.Id, active);
            active.Dispose();
        }

        _logger.LogInformation("QuasiTV: guide channel stream ended");
    }

    /// <summary>Play one library item from <paramref name="offset"/>. If <paramref name="playFor"/> is set (credits cut short),
    /// stop there with a 1.5 s fade to black + audio fade.</summary>
    private async Task RunFfmpeg(string path, string audioMap, TimeSpan offset, TimeSpan? playFor, double tsOffsetSeconds, bool useHw, string banner, double burstSeconds, IReadOnlyList<double>? pops, CancellationToken ct)
    {
        var inv = CultureInfo.InvariantCulture;
        var maxH = Math.Clamp(Config.MaxHeight, 360, 1080);

        // deinterlace DVDs, square the pixels, cap the height, normalize to 29.97 fps
        var vf = $"yadif=deint=interlaced,scale=trunc(iw*sar/2)*2:ih,setsar=1,scale=-2:'min({maxH},ih)',fps=30000/1001" + banner;
        var af = string.Empty;

        // Starting a program from the top (after a bumper / previous show): short fade in.
        if (offset < TimeSpan.FromSeconds(0.5))
        {
            vf += ",fade=t=in:st=0:d=0.5";
            af = "afade=t=in:st=0:d=0.3";
        }

        if (playFor is TimeSpan pf && pf > TimeSpan.Zero)
        {
            var fadeAt = Math.Max(0, pf.TotalSeconds - 1.5).ToString("0.000", inv);
            var fadeLen = Math.Min(1.5, pf.TotalSeconds).ToString("0.000", inv);
            vf += $",fade=t=out:st={fadeAt}:d={fadeLen}";
            af = (af.Length > 0 ? af + "," : string.Empty) + $"afade=t=out:st={fadeAt}:d={fadeLen}";
        }

        var args = HwInit(useHw);
        if (burstSeconds > 0.5)
        {
            args.AddRange(new[] { "-readrate", "1", "-readrate_initial_burst", burstSeconds.ToString("0.000", inv) });
        }
        else
        {
            args.Add("-re");
        }

        args.AddRange(new[] { "-ss", offset.TotalSeconds.ToString("0.000", inv), "-i", path });

        // Pop-Up Video "bloops": a second input, mixed in under the video's own sound at each bubble.
        var bloop = pops is { Count: > 0 } ? PopupVideo.Sound() : null;
        if (bloop is not null)
        {
            args.AddRange(new[] { "-i", bloop });
        }

        if (playFor is TimeSpan t && t > TimeSpan.Zero)
        {
            args.AddRange(new[] { "-t", t.TotalSeconds.ToString("0.000", inv) });
        }

        if (bloop is null)
        {
            args.AddRange(new[] { "-map", "0:v:0", "-map", audioMap });
            args.AddRange(new[] { "-vf", useHw ? vf + ",format=nv12,hwupload" : vf });
            if (af.Length > 0)
            {
                args.AddRange(new[] { "-af", af });
            }
        }
        else
        {
            var n = pops!.Count;
            var fc = new StringBuilder();
            fc.Append(inv, $"[0:v:0]{(useHw ? vf + ",format=nv12,hwupload" : vf)}[pv];");
            fc.Append(inv, $"[{audioMap.TrimEnd('?')}]{(af.Length > 0 ? af : "anull")},aresample=48000,aformat=channel_layouts=stereo[pm];");
            fc.Append(inv, $"[1:a]aresample=48000,aformat=channel_layouts=stereo,volume=0.4,asplit={n}");
            for (var i = 0; i < n; i++)
            {
                fc.Append(inv, $"[ps{i}]");
            }

            fc.Append(';');
            var mixIn = new StringBuilder("[pm]");
            for (var i = 0; i < n; i++)
            {
                var ms = Math.Max(0, (int)Math.Round(pops[i] * 1000));
                fc.Append(inv, $"[ps{i}]adelay={ms}:all=1[pd{i}];");
                mixIn.Append(inv, $"[pd{i}]");
            }

            fc.Append(inv, $"{mixIn}amix=inputs={n + 1}:duration=first:normalize=0[pa]");
            args.AddRange(new[] { "-filter_complex", fc.ToString(), "-map", "[pv]", "-map", "[pa]" });
        }

        args.AddRange(EncoderArgs(useHw, tsOffsetSeconds));
        await Pump(args, ct).ConfigureAwait(false);
    }

    /// <summary>Play an "Up Next" bumper (inputs + filter graph from <see cref="Bumper.Build"/>).</summary>
    private async Task RunBumper(List<string> bumperArgs, double tsOffsetSeconds, bool useHw, CancellationToken ct)
    {
        var args = HwInit(useHw);
        var graph = bumperArgs.ToList();
        if (useHw)
        {
            var i = graph.IndexOf("-filter_complex");
            graph[i + 1] = graph[i + 1].Replace("format=yuv420p[bv]", "format=nv12,hwupload[bv]", StringComparison.Ordinal);
        }

        args.AddRange(graph);
        args.AddRange(new[] { "-map", "[bv]", "-map", "[ba]" });
        args.AddRange(EncoderArgs(useHw, tsOffsetSeconds));
        await Pump(args, ct).ConfigureAwait(false);
    }

    private static List<string> HwInit(bool useHw)
    {
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin" };
        if (useHw)
        {
            args.AddRange(new[] { "-init_hw_device", $"vaapi=va:{Config.VaapiDevice}", "-filter_hw_device", "va" });
        }

        return args;
    }

    /// <summary>Same output format for programs and bumpers, so they join into one continuous stream.</summary>
    private static IEnumerable<string> EncoderArgs(bool useHw, double tsOffsetSeconds)
    {
        var inv = CultureInfo.InvariantCulture;
        var kbps = Math.Clamp(Config.VideoBitrateKbps, 500, 20000);
        var args = new List<string>();
        if (useHw)
        {
            args.AddRange(new[] { "-c:v", "h264_vaapi", "-profile:v", "high", "-rc_mode", "VBR", "-bf", "0" });
        }
        else
        {
            args.AddRange(new[] { "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-level", "4.1", "-pix_fmt", "yuv420p" });
        }

        args.AddRange(new[]
        {
            "-g", "60",
            "-b:v", kbps.ToString(inv) + "k", "-maxrate", (kbps * 3 / 2).ToString(inv) + "k", "-bufsize", (kbps * 2).ToString(inv) + "k",
            "-c:a", "aac", "-ac", "2", "-ar", "48000", "-b:a", "192k",
            "-sn", "-dn",
            "-f", "mpegts", "-muxdelay", "0",
            "-output_ts_offset", tsOffsetSeconds.ToString("0.000", inv),
            "pipe:1"
        });
        return args;
    }

    private async Task Pump(List<string> args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_mediaEncoder.EncoderPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }

        using var proc = new Process { StartInfo = psi };
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
            {
                _logger.LogWarning("QuasiTV ffmpeg: {Line}", e.Data);
            }
        };

        proc.Start();
        proc.BeginErrorReadLine();

        try
        {
            await proc.StandardOutput.BaseStream.CopyToAsync(Response.Body, 81920, ct).ConfigureAwait(false);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            if (!proc.HasExited)
            {
                try
                {
                    proc.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                    // already gone
                }
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Audio track to play: the first track in a preferred language (skipping commentary),
    /// else the file's default track, else the first one. Falls back to "0:a:0?" if Jellyfin has no stream info.</summary>
    private string AudioMap(BaseItem item)
    {
        try
        {
            var audio = item.GetMediaStreams().Where(m => m.Type == MediaStreamType.Audio).OrderBy(m => m.Index).ToList();
            if (audio.Count <= 1)
            {
                return "0:a:0?";
            }

            static bool Commentary(MediaStream m) => (m.Title ?? string.Empty).Contains("comment", StringComparison.OrdinalIgnoreCase);
            var prefs = (Config.PreferredAudioLanguages ?? string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var lang in prefs)
            {
                var hit = audio.FirstOrDefault(m => !Commentary(m) && string.Equals(m.Language, lang, StringComparison.OrdinalIgnoreCase));
                if (hit is not null)
                {
                    return "0:" + hit.Index.ToString(CultureInfo.InvariantCulture);
                }
            }

            var pick = audio.FirstOrDefault(m => m.IsDefault && !Commentary(m)) ?? audio[0];
            return "0:" + pick.Index.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "QuasiTV: no stream info for {Item}; using the first audio track", item.Name);
            return "0:a:0?";
        }
    }

    private static string? WideArt(BaseItem item)
    {
        if (item.HasImage(ImageType.Thumb, 0))
        {
            return "Thumb";
        }

        if (item.HasImage(ImageType.Backdrop, 0))
        {
            return "Backdrop";
        }

        return item.HasImage(ImageType.Primary, 0) ? "Primary" : null;
    }

    private static string XmlTvTime(DateTime utc)
        => utc.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + " +0000";
}
