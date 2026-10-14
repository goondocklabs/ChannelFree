using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.QuasiTV.Channels;

/// <summary>A Jellyfin collection (box set) and the playable movies/shows in it.</summary>
public sealed record Collection(Guid Id, string Name, IReadOnlyList<Guid> Members);

/// <summary>Snapshot of the playable library: movies, shows, each show's episodes, and collections.</summary>
public sealed class LibraryIndex
{
    private LibraryIndex(
        IReadOnlyList<BaseItem> movies,
        IReadOnlyList<BaseItem> musicVideos,
        IReadOnlyList<Series> series,
        IReadOnlyDictionary<Guid, IReadOnlyList<Episode>> episodesBySeries,
        IReadOnlyDictionary<Guid, BaseItem> playable,
        IReadOnlyDictionary<Guid, Collection> collections)
    {
        Movies = movies;
        MusicVideos = musicVideos;
        Series = series;
        EpisodesBySeries = episodesBySeries;
        Playable = playable;
        Collections = collections;
        SeriesById = series.ToDictionary(x => x.Id);
    }

    public IReadOnlyDictionary<Guid, Collection> Collections { get; }

    public IReadOnlyDictionary<Guid, Series> SeriesById { get; }

    public IReadOnlyList<BaseItem> Movies { get; }

    /// <summary>Music videos (their own library type in Jellyfin). Never mixed into movie or genre channels on their own.</summary>
    public IReadOnlyList<BaseItem> MusicVideos { get; }

    /// <summary>Shows that have at least one playable episode.</summary>
    public IReadOnlyList<Series> Series { get; }

    public IReadOnlyDictionary<Guid, IReadOnlyList<Episode>> EpisodesBySeries { get; }

    /// <summary>Every playable movie/episode by id.</summary>
    public IReadOnlyDictionary<Guid, BaseItem> Playable { get; }

    public static LibraryIndex Build(ILibraryManager library)
    {
        static bool Ok(BaseItem i) => !string.IsNullOrEmpty(i.Path) && (i.RunTimeTicks ?? 0) > TimeSpan.TicksPerMinute;

        var movies = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Movie },
                Recursive = true,
                IsVirtualItem = false
            })
            .Where(Ok)
            .ToList();

        var musicVideos = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.MusicVideo },
                Recursive = true,
                IsVirtualItem = false
            })
            .Where(i => !string.IsNullOrEmpty(i.Path) && (i.RunTimeTicks ?? 0) > TimeSpan.TicksPerSecond * 30)
            .ToList();

        var episodes = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Episode },
                Recursive = true,
                IsVirtualItem = false
            })
            .OfType<Episode>()
            .Where(Ok)
            .ToList();

        var bySeries = episodes
            .GroupBy(e => e.SeriesId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Episode>)g.ToList());

        var series = library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.Series },
                Recursive = true
            })
            .OfType<Series>()
            .Where(s => bySeries.ContainsKey(s.Id))
            .ToList();

        var playable = new Dictionary<Guid, BaseItem>();
        foreach (var m in movies)
        {
            playable[m.Id] = m;
        }

        foreach (var e in episodes)
        {
            playable[e.Id] = e;
        }

        foreach (var v in musicVideos)
        {
            playable[v.Id] = v;
        }

        var seriesIds = series.Select(x => x.Id).ToHashSet();
        var collections = new Dictionary<Guid, Collection>();
        foreach (var box in library.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { BaseItemKind.BoxSet },
                Recursive = true
            }).OfType<BoxSet>())
        {
            var members = box.GetLinkedChildren()
                .Where(c => playable.ContainsKey(c.Id) || seriesIds.Contains(c.Id))
                .Select(c => c.Id)
                .Distinct()
                .ToList();
            if (members.Count > 0)
            {
                collections[box.Id] = new Collection(box.Id, box.Name ?? "Collection", members);
            }
        }

        return new LibraryIndex(movies, musicVideos, series, bySeries, playable, collections);
    }
}
