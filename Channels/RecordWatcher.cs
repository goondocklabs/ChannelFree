using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Model.LiveTv;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>
/// Turns the guide's "Record" button into channel control for QuasiTV channels:
///   Record on the program that's on now  -> restart it from the beginning
///   Record on a later program            -> jump so it starts now (revertible for a few minutes)
/// The recording request itself is cancelled immediately.
/// (Jellyfin doesn't raise TimerCreated for its built-in tuner service, so we poll — cheap, in-memory.)
/// </summary>
public sealed class RecordWatcher : IHostedService, IDisposable
{
    private static readonly TimeSpan PollEvery = TimeSpan.FromSeconds(2);

    private readonly ILiveTvManager _liveTv;
    private readonly ILibraryManager _library;
    private readonly ILogger<RecordWatcher> _logger;
    private readonly HashSet<string> _handled = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public RecordWatcher(ILiveTvManager liveTv, ILibraryManager library, ITaskManager tasks, IApplicationPaths paths, ILogger<RecordWatcher> logger)
    {
        _liveTv = liveTv;
        _library = library;
        _logger = logger;
        GuideRefresher.Init(tasks, paths.CachePath, logger, library);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => Loop(_cts.Token), CancellationToken.None);

        // Every 15 min: pick up library changes and lineup changes (first check 30 s after startup).
        _ = Task.Run(
            async () =>
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false);
                    while (!_cts.Token.IsCancellationRequested)
                    {
                        try
                        {
                            // Rebuild channels from the library, notice new titles/episodes, re-plan past the next few hours.
                            ChannelScheduler.Invalidate();
                            var now = DateTime.UtcNow;
                            foreach (var ch in ChannelScheduler.GetChannels(_library))
                            {
                                ChannelScheduler.Current(ch, now);
                            }

                            // Only refresh the guide for lineup changes, and only when nobody's watching.
                            if (ChannelScheduler.LineupChanged(_library))
                            {
                                GuideRefresher.Request(waitUntilIdle: true);
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogDebug(ex, "QuasiTV: library check failed");
                        }

                        await Task.Delay(TimeSpan.FromMinutes(15), _cts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // shutting down
                }
            },
            CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_cts is null)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // stopping
            }
        }
    }

    public void Dispose() => _cts?.Dispose();

    private async Task Loop(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(PollEvery);
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await CheckTimers(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "QuasiTV: timer check failed");
            }
        }
    }

    private async Task CheckTimers(CancellationToken ct)
    {
        if (Plugin.Instance is null)
        {
            return;
        }

        var timers = await _liveTv.GetTimers(new TimerQuery(), ct).ConfigureAwait(false);
        foreach (var t in timers.Items)
        {
            if (string.IsNullOrEmpty(t.Id) || _handled.Contains(t.Id))
            {
                continue;
            }

            var channel = ChannelScheduler.FindByName(_library, t.ChannelName);
            if (channel is null)
            {
                continue; // not one of ours
            }

            _handled.Add(t.Id);

            // Cancel the "recording" first so Jellyfin doesn't tune in to record.
            try
            {
                await _liveTv.CancelTimer(t.Id).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "QuasiTV: couldn't cancel timer {Id}", t.Id);
            }

            var now = DateTime.UtcNow;
            var start = DateTime.SpecifyKind(t.StartDate, DateTimeKind.Utc);
            var slot = ChannelScheduler.Slots(channel, start.AddSeconds(1), start.AddSeconds(2)).FirstOrDefault();
            if (slot is null)
            {
                continue;
            }

            var config = Plugin.Instance!.Configuration;
            if (slot.StartUtc <= now && !config.AllowRestart)
            {
                _logger.LogInformation("QuasiTV: Record on {Title} ignored (restart is turned off)", slot.Item.Name);
                continue;
            }

            if (slot.StartUtc > now && !config.AllowJumpAhead)
            {
                _logger.LogInformation("QuasiTV: Record on later program {Title} ignored (jumping ahead is turned off)", slot.Item.Name);
                continue;
            }

            if (slot.StartUtc <= now)
            {
                // Record on what's on now -> restart it.
                JumpManager.Clear(channel.Id);
                ChannelScheduler.MoveToNow(channel, slot.StartUtc);
                _logger.LogInformation("QuasiTV: Record -> restart {Title} on {Channel}", slot.Item.Name, channel.Name);
            }
            else
            {
                JumpManager.JumpTo(channel, slot.StartUtc);
                _logger.LogInformation("QuasiTV: Record -> jump to {Title} on {Channel}", slot.Item.Name, channel.Name);
            }

            StreamRegistry.Nudge(channel.Id);
            GuideRefresher.Request();
        }

        // Don't let the handled list grow forever.
        if (_handled.Count > 500)
        {
            _handled.Clear();
        }
    }
}
