using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TheIntroDB.Api;

/// <summary>
/// Asks the configured data source for an item's segments and falls back to the
/// other source when the configured one has no data for it — or cannot address it
/// at all, which is the normal case for introdb.app on a library without IMDb ids.
/// </summary>
/// <remarks>
/// The fallback is what makes the switch usable rather than merely configurable:
/// introdb.app only indexes IMDb ids and only stores the aired episode order, while
/// TheIntroDB covers TVDB/TMDB ids and converts other orders. Whichever answers
/// first wins, and a miss is only believed once both had their turn.
/// </remarks>
public sealed class IntroSegmentRouter
{
    private readonly ILogger _logger;
    private readonly IntroDataSource _primary;
    private readonly Func<IntroDataSource, IIntroSegmentSource> _sourceFactory;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroSegmentRouter"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="primary">The configured (primary) data source.</param>
    /// <param name="sourceFactory">Creates the client for a source; injectable for tests.</param>
    public IntroSegmentRouter(
        ILogger logger,
        IntroDataSource primary,
        Func<IntroDataSource, IIntroSegmentSource> sourceFactory)
    {
        _logger = logger;
        _primary = primary;
        _sourceFactory = sourceFactory;
    }

    /// <summary>
    /// Gets the configured (primary) data source.
    /// </summary>
    public IntroDataSource Primary => _primary;

    /// <summary>
    /// Creates a router from the plugin configuration.
    /// </summary>
    /// <param name="httpClient">HTTP client shared by both sources.</param>
    /// <param name="plugin">Plugin instance, for the configuration and the API key.</param>
    /// <param name="logger">Logger instance.</param>
    /// <returns>The router.</returns>
    public static IntroSegmentRouter Create(HttpClient httpClient, Plugin plugin, ILogger logger)
    {
        var primary = IntroDataSources.Parse(plugin.Configuration?.DataSource);
        return new IntroSegmentRouter(
            logger,
            primary,
            source => source == IntroDataSource.IntroDbApp
                ? new IntroDbAppClient(httpClient, logger)
                : new TheIntroDbClient(httpClient, plugin, logger));
    }

    /// <summary>
    /// Returns the sources that can address the request, in the order they will be
    /// asked: the configured source first, the other one as fallback.
    /// </summary>
    /// <param name="request">The lookup to address.</param>
    /// <returns>The addressable sources, possibly empty.</returns>
    public IReadOnlyList<IIntroSegmentSource> GetResolutionOrder(IntroLookupRequest request)
    {
        var order = new List<IIntroSegmentSource>(2);
        if (request is null)
        {
            return order;
        }

        var primary = _sourceFactory(_primary);
        if (primary.CanAddress(request))
        {
            order.Add(primary);
        }
        else
        {
            _logger.LogDebug(
                "{Source} cannot address this item; trying the other source. IsMovie={IsMovie}, Season={Season}, Episode={Episode}, Order={Order}",
                _primary.GetDisplayName(),
                request.IsMovie,
                request.Season,
                request.Episode,
                request.Mapping ?? (request.OrderUnsupported ? "unsupported" : "default"));
        }

        var fallback = _sourceFactory(_primary.Other());
        if (fallback.CanAddress(request))
        {
            order.Add(fallback);
        }

        return order;
    }

    /// <summary>
    /// Fetches the item's segments from the first source that can answer.
    /// </summary>
    /// <param name="request">The lookup to perform.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The outcome. A definitive miss is reported only when every addressable source
    /// missed; a transient failure anywhere outranks a miss, so stored segments are
    /// never deleted on the strength of a flaky request.
    /// </returns>
    public async Task<IntroLookupOutcome> FetchAsync(IntroLookupRequest request, CancellationToken cancellationToken)
    {
        var order = GetResolutionOrder(request);
        var anyRateLimited = false;
        var anyError = false;

        foreach (var source in order)
        {
            if (source.IsRateLimitActive(request))
            {
                anyRateLimited = true;
                continue;
            }

            var result = await source.FetchAsync(request, cancellationToken).ConfigureAwait(false);

            if (result.IsRateLimited)
            {
                anyRateLimited = true;
                continue;
            }

            if (result.IsError)
            {
                anyError = true;
                continue;
            }

            if (result.IsNotFound)
            {
                _logger.LogDebug("{Source} has no segments for this item; trying the other source", source.Source.GetDisplayName());
                continue;
            }

            return IntroLookupOutcome.Answered(result, source.Source);
        }

        if (anyError)
        {
            return IntroLookupOutcome.Error();
        }

        if (anyRateLimited)
        {
            return IntroLookupOutcome.RateLimited();
        }

        return IntroLookupOutcome.NotFound();
    }
}
