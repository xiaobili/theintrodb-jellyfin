namespace TheIntroDB.Api;

/// <summary>
/// A service this plugin can read intro, recap and credits timestamps from.
/// </summary>
public enum IntroDataSource
{
    /// <summary>
    /// theintrodb.org (API v4). Addresses media by TVDB, TMDB or IMDb id, knows
    /// episode orders, and serves intro, recap, credits and preview.
    /// </summary>
    TheIntroDb = 0,

    /// <summary>
    /// introdb.app. Addresses media by IMDb id only, needs no key, and serves
    /// intro, recap and outro (plus post-credit scenes, which Jellyfin has no
    /// segment type for).
    /// </summary>
    IntroDbApp = 1
}
