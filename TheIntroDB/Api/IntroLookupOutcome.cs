namespace TheIntroDB.Api;

/// <summary>
/// Result of a routed lookup: what the sources answered, and which one answered it.
/// A miss is only reported when every source that could address the item missed, so
/// a definitive "no data" is never inferred from one source's silence.
/// </summary>
public sealed class IntroLookupOutcome
{
    private IntroLookupOutcome(MediaFetchResult result, IntroDataSource? answeredBy)
    {
        Result = result;
        AnsweredBy = answeredBy;
    }

    /// <summary>
    /// Gets the fetch result to act on. <see cref="MediaFetchResult.IsNotFound"/> is
    /// set only when every addressable source reported a miss.
    /// </summary>
    public MediaFetchResult Result { get; }

    /// <summary>
    /// Gets the source that answered, or null when none did.
    /// </summary>
    public IntroDataSource? AnsweredBy { get; }

    /// <summary>
    /// Creates an outcome for an answered lookup.
    /// </summary>
    /// <param name="result">The source's result.</param>
    /// <param name="source">The source that answered.</param>
    /// <returns>The outcome.</returns>
    public static IntroLookupOutcome Answered(MediaFetchResult result, IntroDataSource source)
        => new(result, source);

    /// <summary>
    /// Creates a definitive miss: every addressable source has no data for the item.
    /// </summary>
    /// <returns>The outcome.</returns>
    public static IntroLookupOutcome NotFound() => new(MediaFetchResult.NotFound(), null);

    /// <summary>
    /// Creates an outcome where no source answered because they are all parked by a
    /// rate limit. Transient: the caller must keep whatever segments it already has.
    /// </summary>
    /// <returns>The outcome.</returns>
    public static IntroLookupOutcome RateLimited() => new(MediaFetchResult.RateLimited(), null);

    /// <summary>
    /// Creates an outcome where a source failed transiently and none answered.
    /// </summary>
    /// <returns>The outcome.</returns>
    public static IntroLookupOutcome Error() => new(MediaFetchResult.Error(), null);
}
