using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace TheIntroDB.Api;

/// <summary>
/// HTTP client for introdb.app (<c>GET /segments</c>).
/// </summary>
/// <remarks>
/// <para>
/// The service addresses a media by IMDb id alone — season and episode for TV,
/// <c>is_movie=true</c> for a movie — and publishes no burst figure for its
/// anonymous read endpoint. Pacing therefore sits at ten requests per second,
/// which a library scan will never queue against and which stays far below
/// anything that looks like abuse.
/// </para>
/// <para>
/// A media the database has not timed answers <c>200</c> with every segment
/// <c>null</c> rather than <c>404</c>, so "no data" is read from the body, not
/// from the status code.
/// </para>
/// </remarks>
public sealed class IntroDbAppClient : IIntroSegmentSource
{
    internal const string BaseUrl = "https://api.introdb.app";

    private const int RequestsPerWindow = 10;
    private const int RateResetClampSeconds = 5 * 60;
    private const int DefaultRetryAfterSeconds = 5 * 60;
    private const int MaxConsecutiveRateLimitMultiplier = 8;
    private const long UnixSecondsFloor = 1_000_000_000;

    private static readonly TimeSpan RateLimitWindow = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Spacing between request starts, for <see cref="RequestsPerWindow"/> per second.
    /// </summary>
    internal static readonly TimeSpan MinDelayBetweenRequests = TimeSpan.FromMilliseconds(RateLimitWindow.TotalMilliseconds / RequestsPerWindow);

    private static readonly SemaphoreSlim RateLimitLock = new(1, 1);

    private static DateTime _lastRequestUtc = DateTime.MinValue;
    private static DateTime _nextAllowedSendUtc = DateTime.MinValue;
    private static long _rateLimitExpiryTicks;
    private static long _lastSkipLoggedExpiryTicks;
    private static int _consecutiveRateLimits;

    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="IntroDbAppClient"/> class.
    /// </summary>
    /// <param name="httpClient">HTTP client for requests.</param>
    /// <param name="logger">Logger instance.</param>
    public IntroDbAppClient(HttpClient httpClient, ILogger logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <inheritdoc />
    public IntroDataSource Source => IntroDataSource.IntroDbApp;

    /// <inheritdoc />
    public bool CanAddress(IntroLookupRequest request)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.IntroDbAppImdbId))
        {
            return false;
        }

        if (request.IsMovie)
        {
            return true;
        }

        // introdb.app stores one numbering per show — the aired one — and cannot
        // convert another, so an episode in a DVD/alternate/absolute order would be
        // answered with a different episode's timestamps. No segments beats wrong
        // segments; the other source can still resolve those.
        return request.HasEpisodeNumbers && request.IsDefaultOrder;
    }

    /// <inheritdoc />
    public bool IsRateLimitActive(IntroLookupRequest request)
    {
        var expiryUtc = GetRateLimitExpiryUtc();
        if (DateTime.UtcNow >= expiryUtc)
        {
            return false;
        }

        if (expiryUtc.Ticks != Interlocked.Read(ref _lastSkipLoggedExpiryTicks))
        {
            // One warning per back-off period instead of one per skipped item.
            Interlocked.Exchange(ref _lastSkipLoggedExpiryTicks, expiryUtc.Ticks);
            _logger.LogWarning(
                "IntroDB API rate limit is currently active. Skipping request. The rate limit will reset at {RateLimitExpiryUtc} UTC.",
                expiryUtc);
        }
        else
        {
            _logger.LogDebug(
                "IntroDB API rate limit is currently active until {RateLimitExpiryUtc} UTC; skipping {MediaType}.",
                expiryUtc,
                request?.IsMovie == true ? "movie" : "episode");
        }

        return true;
    }

    /// <inheritdoc />
    public async Task<MediaFetchResult> FetchAsync(IntroLookupRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!CanAddress(request))
        {
            return MediaFetchResult.NotFound();
        }

        if (IsRateLimitActive(request))
        {
            return MediaFetchResult.RateLimited();
        }

        var imdbId = request.IntroDbAppImdbId!.Trim();
        var requestUri = new Uri(BaseUrl + "/segments" + BuildSegmentsQuery(imdbId, request.IsMovie, request.Season, request.Episode), UriKind.Absolute);
        _logger.LogInformation("IntroDB API request: {Uri}", requestUri);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, requestUri);
        httpRequest.Headers.TryAddWithoutValidation("Accept", "application/json");
        var version = typeof(IntroDbAppClient).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        httpRequest.Headers.UserAgent.Clear();
        httpRequest.Headers.UserAgent.Add(new ProductInfoHeaderValue("theintrodb-jellyfin-plugin", version));

        try
        {
            await WaitForRateLimitAsync(cancellationToken).ConfigureAwait(false);
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("IntroDB API response: StatusCode={StatusCode} for {Uri}", response.StatusCode, requestUri);

            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var retryAfterSeconds = GetRetryAfterSeconds(response.Headers, body);
                var consecutive = Interlocked.Increment(ref _consecutiveRateLimits);
                var waitSeconds = ApplyConsecutiveBackOff(retryAfterSeconds, consecutive);

                SetRateLimitExpiryUtc(DateTime.UtcNow.AddSeconds(waitSeconds));
                _logger.LogWarning(
                    "IntroDB API rate limit exceeded. Will not send requests until {RateLimitExpiryUtc} UTC. Retry-after: {RetryAfterSeconds}s. Consecutive 429 responses: {Consecutive429Count}",
                    GetRateLimitExpiryUtc(),
                    retryAfterSeconds,
                    consecutive);

                return MediaFetchResult.RateLimited();
            }

            Interlocked.Exchange(ref _consecutiveRateLimits, 0);
            UpdateRateWindowFromHeaders(response.Headers);

            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                _logger.LogWarning(
                    "IntroDB API error response: StatusCode={StatusCode}, Body={Body}",
                    (int)response.StatusCode,
                    string.IsNullOrEmpty(body) ? "(empty)" : body.Length > 500 ? body[..500] + "..." : body);

                // 400 means the request itself cannot be addressed (an id shape
                // introdb.app does not accept). That is deterministic for the item,
                // so it is a miss rather than a transient failure — but it is worth
                // a warning, because it means our addressing was wrong.
                return response.StatusCode == HttpStatusCode.NotFound || response.StatusCode == HttpStatusCode.BadRequest
                    ? MediaFetchResult.NotFound()
                    : MediaFetchResult.Error();
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var parsed = JsonSerializer.Deserialize<IntroDbAppSegmentsResponse>(json);
            var media = MapToMediaResponse(parsed);
            if (media is null)
            {
                _logger.LogDebug(
                    "IntroDB API has no segments for imdb_id={ImdbId} (confirmed absent: intro={NoIntro}, recap={NoRecap}, outro={NoOutro})",
                    imdbId,
                    parsed?.NoIntro,
                    parsed?.NoRecap,
                    parsed?.NoOutro);
                return MediaFetchResult.NotFound();
            }

            _logger.LogDebug(
                "IntroDB API parsed response: IntroCount={IntroCount}, RecapCount={RecapCount}, OutroCount={OutroCount}",
                media.Intro.Count,
                media.Recap.Count,
                media.Credits.Count);
            return MediaFetchResult.Success(media);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cancellation is cooperative — propagate it rather than masking it as
            // a transient API error.
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IntroDB API request failed for {Uri}", requestUri);
            return MediaFetchResult.Error();
        }
    }

    /// <summary>
    /// Builds the query string for <c>GET /segments</c>: an IMDb id plus either the
    /// season and episode (TV) or <c>is_movie=true</c> (movie).
    /// </summary>
    /// <param name="imdbId">IMDb id, e.g. <c>tt0903747</c>.</param>
    /// <param name="isMovie">True for a movie.</param>
    /// <param name="season">Season number; required for TV.</param>
    /// <param name="episode">Episode number; required for TV.</param>
    /// <returns>The query string, starting with '?'.</returns>
    internal static string BuildSegmentsQuery(string imdbId, bool isMovie, int? season, int? episode)
    {
        var queryParams = new List<string>(3)
        {
            "imdb_id=" + Uri.EscapeDataString(imdbId.Trim())
        };

        if (isMovie)
        {
            queryParams.Add("is_movie=true");
        }
        else
        {
            queryParams.Add($"season={season}");
            queryParams.Add($"episode={episode}");
        }

        return "?" + string.Join("&", queryParams);
    }

    /// <summary>
    /// Translates an introdb.app response into the plugin's neutral shape: intro and
    /// recap as they are, <c>outro</c> as credits (Jellyfin's outro), and nothing for
    /// <c>post_credits</c> — Jellyfin has no segment type for a post-credit scene, and
    /// serving it as an outro would put a second skip-credits marker on the item.
    /// </summary>
    /// <param name="response">The parsed response.</param>
    /// <returns>The neutral response, or null when every segment is absent.</returns>
    internal static MediaResponse? MapToMediaResponse(IntroDbAppSegmentsResponse? response)
    {
        if (response is null)
        {
            return null;
        }

        var media = new MediaResponse
        {
            Type = response.IsMovie == true ? "movie" : "tv",
            Season = response.Season,
            Episode = response.Episode
        };

        AddTimestamp(media.Intro, response.Intro);
        AddTimestamp(media.Recap, response.Recap);
        AddTimestamp(media.Credits, response.Outro);

        // A body of nothing but nulls is "not timed yet" (or confirmed absent), not
        // an empty dataset to store: reporting it as a miss lets the caller fall back
        // to the other source and remember the miss.
        return media.Intro.Count == 0 && media.Recap.Count == 0 && media.Credits.Count == 0
            ? null
            : media;
    }

    /// <summary>
    /// Computes how long to wait after a 429, from the <c>Retry-After</c> or
    /// <c>X-RateLimit-Reset</c> header, or from the <c>reset_at</c> the service puts
    /// in the body (an ISO timestamp or Unix seconds).
    /// </summary>
    /// <param name="headers">Response headers.</param>
    /// <param name="body">Response body, used for <c>reset_at</c>.</param>
    /// <returns>Seconds to wait before the next request.</returns>
    internal static int GetRetryAfterSeconds(HttpResponseHeaders headers, string? body)
    {
        if (headers.RetryAfter?.Delta is TimeSpan delta)
        {
            return ClampRetryAfterSeconds((int)Math.Ceiling(delta.TotalSeconds));
        }

        if (headers.RetryAfter?.Date is DateTimeOffset date)
        {
            return ClampRetryAfterSeconds((int)Math.Ceiling((date.UtcDateTime - DateTime.UtcNow).TotalSeconds));
        }

        if (headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            && int.TryParse(resetValues.FirstOrDefault(), out var resetSeconds)
            && resetSeconds > 0)
        {
            return ClampRetryAfterSeconds(resetSeconds);
        }

        return GetResetFromBody(body) ?? DefaultRetryAfterSeconds;
    }

    /// <summary>
    /// Grows the back-off wait across consecutive 429 responses so a still-exhausted
    /// window is probed less and less often instead of at a constant rate.
    /// </summary>
    /// <param name="baseSeconds">Base wait from the response headers.</param>
    /// <param name="consecutive">Number of consecutive 429 responses.</param>
    /// <returns>Adjusted wait in seconds, capped at the rate-limit ceiling.</returns>
    internal static int ApplyConsecutiveBackOff(int baseSeconds, int consecutive)
    {
        if (consecutive <= 1)
        {
            return baseSeconds;
        }

        var multiplier = Math.Min(consecutive, MaxConsecutiveRateLimitMultiplier);
        return Math.Min(baseSeconds * multiplier, RateResetClampSeconds);
    }

    /// <summary>
    /// Reads the current back-off expiry.
    /// </summary>
    /// <returns>The UTC instant requests may resume.</returns>
    internal static DateTime GetRateLimitExpiryUtc()
        => new(Interlocked.Read(ref _rateLimitExpiryTicks), DateTimeKind.Utc);

    private static void SetRateLimitExpiryUtc(DateTime expiryUtc)
        => Interlocked.Exchange(ref _rateLimitExpiryTicks, expiryUtc.Ticks);

    private static void AddTimestamp(Collection<SegmentTimestamp> target, IntroDbAppSegment? segment)
    {
        if (segment is null)
        {
            return;
        }

        // introdb.app sends the offset in seconds and in milliseconds; prefer the
        // millisecond fields, which keep a fractional second exact.
        var startMs = segment.StartMs ?? ToMilliseconds(segment.StartSec);
        var endMs = segment.EndMs ?? ToMilliseconds(segment.EndSec);
        if (startMs is null && endMs is null)
        {
            return;
        }

        target.Add(new SegmentTimestamp { StartMs = startMs, EndMs = endMs });
    }

    private static long? ToMilliseconds(double? seconds)
        => seconds is null ? null : (long)Math.Round(seconds.Value * 1000, MidpointRounding.AwayFromZero);

    private static int ClampRetryAfterSeconds(int seconds)
        => Math.Max(1, Math.Min(seconds, RateResetClampSeconds));

    private static int? GetResetFromBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("reset_at", out var resetAt))
            {
                return null;
            }

            if (resetAt.ValueKind == JsonValueKind.Number
                && resetAt.TryGetInt64(out var unixSeconds)
                && unixSeconds > UnixSecondsFloor)
            {
                var untilReset = DateTimeOffset.FromUnixTimeSeconds(unixSeconds) - DateTimeOffset.UtcNow;
                return ClampRetryAfterSeconds((int)Math.Ceiling(untilReset.TotalSeconds));
            }

            if (resetAt.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    resetAt.GetString(),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                    out var resetUtc))
            {
                return ClampRetryAfterSeconds((int)Math.Ceiling((resetUtc - DateTimeOffset.UtcNow).TotalSeconds));
            }
        }
        catch (JsonException)
        {
            // Not JSON (e.g. a proxy's plain-text 429): fall back to the default wait.
        }

        return null;
    }

    /// <summary>
    /// Holds the next request until the advertised window resets, when almost no
    /// budget is left. Insurance against other consumers of the same public IP.
    /// </summary>
    /// <param name="headers">Response headers.</param>
    private static void UpdateRateWindowFromHeaders(HttpResponseHeaders headers)
    {
        if (!headers.TryGetValues("X-RateLimit-Remaining", out var remainingValues)
            || !int.TryParse(remainingValues.FirstOrDefault(), out var remaining)
            || remaining > 1)
        {
            return;
        }

        if (!headers.TryGetValues("X-RateLimit-Reset", out var resetValues)
            || !int.TryParse(resetValues.FirstOrDefault(), out var resetSeconds)
            || resetSeconds <= 0)
        {
            return;
        }

        var resetUtc = DateTime.UtcNow.AddSeconds(ClampRetryAfterSeconds(resetSeconds));
        if (resetUtc > _nextAllowedSendUtc)
        {
            _nextAllowedSendUtc = resetUtc;
        }
    }

    /// <summary>
    /// Waits if necessary to respect the pacing, plus any hold imposed by a
    /// nearly-exhausted rate window.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    private static async Task WaitForRateLimitAsync(CancellationToken cancellationToken)
    {
        await RateLimitLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = DateTime.UtcNow;
            var waitUntil = now;
            if (_lastRequestUtc != DateTime.MinValue)
            {
                var pacedSend = _lastRequestUtc + MinDelayBetweenRequests;
                if (pacedSend > waitUntil)
                {
                    waitUntil = pacedSend;
                }
            }

            if (_nextAllowedSendUtc > waitUntil)
            {
                waitUntil = _nextAllowedSendUtc;
            }

            var waitTime = waitUntil - now;
            if (waitTime > TimeSpan.Zero)
            {
                await Task.Delay(waitTime, cancellationToken).ConfigureAwait(false);
            }

            _lastRequestUtc = DateTime.UtcNow;
        }
        finally
        {
            RateLimitLock.Release();
        }
    }
}
