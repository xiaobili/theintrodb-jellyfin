using System;

namespace TheIntroDB.Api;

/// <summary>
/// Naming and parsing of <see cref="IntroDataSource"/>. The configuration stores
/// the id string, so the config page, the router and the providers agree on one
/// spelling.
/// </summary>
public static class IntroDataSources
{
    /// <summary>
    /// Configuration value of <see cref="IntroDataSource.TheIntroDb"/>.
    /// </summary>
    public const string TheIntroDbId = "theintrodb";

    /// <summary>
    /// Configuration value of <see cref="IntroDataSource.IntroDbApp"/>.
    /// </summary>
    public const string IntroDbAppId = "introdbapp";

    /// <summary>
    /// Reads a configured data source id. Unset or unknown values fall back to
    /// <see cref="IntroDataSource.TheIntroDb"/>, so a configuration written by an
    /// older version — or by hand — keeps the previous behaviour instead of
    /// silently scanning against a service the user never chose.
    /// </summary>
    /// <param name="value">The configured id.</param>
    /// <returns>The data source to use as primary.</returns>
    public static IntroDataSource Parse(string? value)
        => string.Equals(value?.Trim(), IntroDbAppId, StringComparison.OrdinalIgnoreCase)
            ? IntroDataSource.IntroDbApp
            : IntroDataSource.TheIntroDb;

    /// <summary>
    /// Returns the id the configuration stores for a source.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>The configuration id.</returns>
    public static string ToId(this IntroDataSource source)
        => source == IntroDataSource.IntroDbApp ? IntroDbAppId : TheIntroDbId;

    /// <summary>
    /// Returns a human-readable name for logs and the config page.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>The display name.</returns>
    public static string GetDisplayName(this IntroDataSource source)
        => source == IntroDataSource.IntroDbApp ? "IntroDB (introdb.app)" : "TheIntroDB (theintrodb.org)";

    /// <summary>
    /// Returns the other source, which answers when this one has no data for an
    /// item or cannot address it.
    /// </summary>
    /// <param name="source">The configured source.</param>
    /// <returns>The fallback source.</returns>
    public static IntroDataSource Other(this IntroDataSource source)
        => source == IntroDataSource.IntroDbApp ? IntroDataSource.TheIntroDb : IntroDataSource.IntroDbApp;
}
