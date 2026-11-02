using System.Text.Json.Serialization;

namespace TheIntroDB.Api;

/// <summary>
/// Response of <c>GET /segments</c> on introdb.app. One object per segment type —
/// missing types are <c>null</c> — and no 404 for a media the database has not
/// timed yet: that answers 200 with every type null.
/// </summary>
/// <remarks>
/// The service serves <c>intro</c>, <c>recap</c>, <c>outro</c> (end credits) and
/// <c>post_credits</c>. There is no preview, and no post-credit segment type in
/// Jellyfin, so <see cref="PostCredits"/> is read but never turned into a segment.
/// </remarks>
public sealed class IntroDbAppSegmentsResponse
{
    /// <summary>
    /// Gets or sets the IMDb id the lookup was addressed by.
    /// </summary>
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; set; }

    /// <summary>
    /// Gets or sets the media type: "tv" or "movie".
    /// </summary>
    [JsonPropertyName("media_type")]
    public string? MediaType { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the media is a movie.
    /// </summary>
    [JsonPropertyName("is_movie")]
    public bool? IsMovie { get; set; }

    /// <summary>
    /// Gets or sets the season number (0 for movies).
    /// </summary>
    [JsonPropertyName("season")]
    public int? Season { get; set; }

    /// <summary>
    /// Gets or sets the episode number (0 for movies).
    /// </summary>
    [JsonPropertyName("episode")]
    public int? Episode { get; set; }

    /// <summary>
    /// Gets or sets the intro, if the database has one.
    /// </summary>
    [JsonPropertyName("intro")]
    public IntroDbAppSegment? Intro { get; set; }

    /// <summary>
    /// Gets or sets the recap, if the database has one.
    /// </summary>
    [JsonPropertyName("recap")]
    public IntroDbAppSegment? Recap { get; set; }

    /// <summary>
    /// Gets or sets the outro (end credits), if the database has one.
    /// </summary>
    [JsonPropertyName("outro")]
    public IntroDbAppSegment? Outro { get; set; }

    /// <summary>
    /// Gets or sets the post-credit scene, if the database has one. Jellyfin has no
    /// segment type for it, so it is never served.
    /// </summary>
    [JsonPropertyName("post_credits")]
    public IntroDbAppSegment? PostCredits { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the community confirmed the episode
    /// has no intro. Such a <c>null</c> intro means "nothing to skip", not "not
    /// timed yet".
    /// </summary>
    [JsonPropertyName("no_intro")]
    public bool NoIntro { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the community confirmed the episode
    /// has no recap.
    /// </summary>
    [JsonPropertyName("no_recap")]
    public bool NoRecap { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the community confirmed the episode
    /// has no outro.
    /// </summary>
    [JsonPropertyName("no_outro")]
    public bool NoOutro { get; set; }
}
