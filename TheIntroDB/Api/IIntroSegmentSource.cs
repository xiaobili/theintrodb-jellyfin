using System.Threading;
using System.Threading.Tasks;

namespace TheIntroDB.Api;

/// <summary>
/// A source of intro, recap and credits timestamps. Implementations answer in the
/// plugin's neutral <see cref="MediaResponse"/> shape, so the segment provider
/// does not care which service answered.
/// </summary>
public interface IIntroSegmentSource
{
    /// <summary>
    /// Gets which data source this is.
    /// </summary>
    IntroDataSource Source { get; }

    /// <summary>
    /// Determines whether this source can address the request at all: it has the
    /// ids it indexes by, the episode numbers a TV lookup needs, and — for an
    /// episode — a numbering it can express.
    /// </summary>
    /// <param name="request">The lookup to address.</param>
    /// <returns>True when a lookup could be sent for this request.</returns>
    bool CanAddress(IntroLookupRequest request);

    /// <summary>
    /// Determines whether this source asked the plugin to stop calling it for now
    /// (HTTP 429 or a local back-off). Implementations warn once per back-off
    /// period rather than once per item.
    /// </summary>
    /// <param name="request">The lookup that would be sent.</param>
    /// <returns>True while the source is parked.</returns>
    bool IsRateLimitActive(IntroLookupRequest request);

    /// <summary>
    /// Fetches the timestamps for one item. HTTP failures are reported through the
    /// result rather than thrown, so a caller can tell a definitive miss from a
    /// transient failure.
    /// </summary>
    /// <param name="request">The lookup to perform.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fetch result.</returns>
    Task<MediaFetchResult> FetchAsync(IntroLookupRequest request, CancellationToken cancellationToken);
}
