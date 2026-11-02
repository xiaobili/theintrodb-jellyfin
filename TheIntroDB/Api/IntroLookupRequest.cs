namespace TheIntroDB.Api;

/// <summary>
/// One media lookup, carrying every id and episode number the library item has.
/// Each data source reads the parts it understands and ignores the rest, so the
/// same request can be answered by either source even though their addressing
/// differs (TheIntroDB: TVDB/TMDB/IMDb, introdb.app: IMDb only).
/// </summary>
public sealed class IntroLookupRequest
{
    /// <summary>
    /// Gets or sets the TMDB id of the movie or series, 0 or null when unknown.
    /// </summary>
    public int? TmdbId { get; set; }

    /// <summary>
    /// Gets or sets the TVDB id of the movie or series, 0 or null when unknown.
    /// TVDB is TheIntroDB's identity, and the only id an episode order can be
    /// expressed against.
    /// </summary>
    public int? TvdbId { get; set; }

    /// <summary>
    /// Gets or sets the IMDb id of the item itself: a movie's, or an episode's own
    /// id when it has one.
    /// </summary>
    public string? ImdbId { get; set; }

    /// <summary>
    /// Gets or sets the IMDb id of the series an episode belongs to. introdb.app
    /// indexes episodes by their show's IMDb id, not the episode's.
    /// </summary>
    public string? SeriesImdbId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the item is a movie rather than an
    /// episode.
    /// </summary>
    public bool IsMovie { get; set; }

    /// <summary>
    /// Gets or sets the season number, in <see cref="Mapping"/>'s order.
    /// </summary>
    public int? Season { get; set; }

    /// <summary>
    /// Gets or sets the episode number, in <see cref="Mapping"/>'s order.
    /// </summary>
    public int? Episode { get; set; }

    /// <summary>
    /// Gets or sets the total video duration in milliseconds, used by TheIntroDB
    /// to pick the matching release version.
    /// </summary>
    public long? DurationMs { get; set; }

    /// <summary>
    /// Gets or sets the episode order the numbers are given in (a TVDB order name
    /// such as "dvd" or "alternate"), or null for the default (aired) order.
    /// </summary>
    public string? Mapping { get; set; }

    /// <summary>
    /// Gets or sets the TVDB episode id, which pins one exact episode record
    /// instead of resolving the numbers.
    /// </summary>
    public long? TvdbEpisodeId { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the series numbers its episodes in
    /// an order no source can convert (an order the plugin cannot name).
    /// </summary>
    public bool OrderUnsupported { get; set; }

    /// <summary>
    /// Gets a value indicating whether the request carries any provider id at all.
    /// </summary>
    public bool HasAnyId => TmdbId is > 0 || TvdbId is > 0 || !string.IsNullOrWhiteSpace(ImdbId);

    /// <summary>
    /// Gets a value indicating whether the request carries the episode numbers a
    /// TV lookup needs.
    /// </summary>
    public bool HasEpisodeNumbers => Season.HasValue && Episode.HasValue;

    /// <summary>
    /// Gets a value indicating whether the numbers are in the default (aired)
    /// order, which is the only order every source stores.
    /// </summary>
    public bool IsDefaultOrder => !OrderUnsupported && string.IsNullOrWhiteSpace(Mapping);

    /// <summary>
    /// Gets which kind of provider id addresses the request, for logs and usage
    /// properties.
    /// </summary>
    public string IdSource => TvdbId is > 0 ? "tvdb" : TmdbId is > 0 ? "tmdb" : !string.IsNullOrWhiteSpace(ImdbId) ? "imdb" : "none";

    /// <summary>
    /// Gets the IMDb id introdb.app is addressed by: a series' id for an episode,
    /// the item's own id for a movie.
    /// </summary>
    public string? IntroDbAppImdbId => IsMovie ? ImdbId : SeriesImdbId ?? ImdbId;
}
