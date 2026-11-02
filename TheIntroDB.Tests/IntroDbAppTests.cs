using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using TheIntroDB.Api;
using TheIntroDB.Providers;
using Xunit;

namespace TheIntroDB.Tests;

/// <summary>
/// Guards the introdb.app source and the source switch: the service is addressed by
/// IMDb id only and answers an untimed media with 200 and all-null segments, so the
/// contract to protect is "a miss means both sources missed" — never that one
/// service's silence deletes another's segments.
/// </summary>
public class IntroDbAppTests
{
    [Fact]
    public void MovieQuerySendsImdbIdAndTheMovieFlag()
    {
        Assert.Equal(
            "?imdb_id=tt0371746&is_movie=true",
            IntroDbAppClient.BuildSegmentsQuery("tt0371746", true, null, null));
    }

    [Fact]
    public void EpisodeQuerySendsSeasonAndEpisode()
    {
        Assert.Equal(
            "?imdb_id=tt0903747&season=1&episode=2",
            IntroDbAppClient.BuildSegmentsQuery("tt0903747", false, 1, 2));
    }

    [Fact]
    public void QueryEscapesTheImdbId()
    {
        Assert.Contains("imdb_id=tt%2009", IntroDbAppClient.BuildSegmentsQuery("tt 09", true, null, null), StringComparison.Ordinal);
    }

    [Fact]
    public void OutroBecomesCreditsAndPostCreditsIsDropped()
    {
        var response = new IntroDbAppSegmentsResponse
        {
            IsMovie = true,
            Intro = new IntroDbAppSegment { StartMs = 2500, EndMs = 58000 },
            Outro = new IntroDbAppSegment { StartSec = 7038, EndSec = 7540 },
            PostCredits = new IntroDbAppSegment { StartMs = 7_540_000, EndMs = 7_580_000 }
        };

        var media = IntroDbAppClient.MapToMediaResponse(response);

        Assert.NotNull(media);
        Assert.Single(media!.Intro);
        Assert.Single(media.Credits);
        Assert.Empty(media.Recap);
        Assert.Empty(media.Preview);

        // Jellyfin has no post-credit segment type: serving it as a second outro would
        // put two "skip credits" markers on the item.
        Assert.DoesNotContain(media.Credits, stamp => stamp.StartMs == 7_540_000);
    }

    [Fact]
    public void MillisecondFieldsWinOverSeconds()
    {
        var response = new IntroDbAppSegmentsResponse
        {
            Intro = new IntroDbAppSegment { StartSec = 2.5, EndSec = 58, StartMs = 2500, EndMs = 58000 }
        };

        var media = IntroDbAppClient.MapToMediaResponse(response);

        Assert.NotNull(media);
        Assert.Equal(2500, media!.Intro[0].StartMs);
        Assert.Equal(58000, media.Intro[0].EndMs);
    }

    [Fact]
    public void FractionalSecondsSurviveWhenOnlySecondsAreSent()
    {
        var response = new IntroDbAppSegmentsResponse
        {
            Intro = new IntroDbAppSegment { StartSec = 2.5, EndSec = 58.25 }
        };

        var media = IntroDbAppClient.MapToMediaResponse(response);

        Assert.NotNull(media);
        Assert.Equal(2500, media!.Intro[0].StartMs);
        Assert.Equal(58250, media.Intro[0].EndMs);
    }

    [Fact]
    public void AnUntimedMediaIsAMissRatherThanAnEmptyDataset()
    {
        // introdb.app answers 200 with every segment null for a media it has not timed.
        Assert.Null(IntroDbAppClient.MapToMediaResponse(new IntroDbAppSegmentsResponse()));
    }

    [Fact]
    public void PostCreditsAloneIsStillAMiss()
    {
        var response = new IntroDbAppSegmentsResponse
        {
            PostCredits = new IntroDbAppSegment { StartMs = 7_540_000, EndMs = 7_580_000 }
        };

        Assert.Null(IntroDbAppClient.MapToMediaResponse(response));
    }

    [Theory]
    [InlineData(null, "theintrodb")]
    [InlineData("", "theintrodb")]
    [InlineData("theintrodb", "theintrodb")]
    [InlineData("introdbapp", "introdbapp")]
    [InlineData("IntroDBApp", "introdbapp")]
    [InlineData(" introdbapp ", "introdbapp")]
    [InlineData("somethingelse", "theintrodb")]
    public void ConfiguredDataSourceFallsBackToTheIntroDbWhenUnknown(string? configured, string expected)
    {
        Assert.Equal(expected, IntroDataSources.Parse(configured).ToId());
    }

    [Fact]
    public void IntroDbAppOnlyAddressesImdbAddressedMediaInTheDefaultOrder()
    {
        using var httpClient = new HttpClient();
        var client = new IntroDbAppClient(httpClient, NullLogger.Instance);

        Assert.True(client.CanAddress(Movie("tt0371746")));
        Assert.False(client.CanAddress(Movie(null)));
        Assert.True(client.CanAddress(Episode()));
        Assert.False(client.CanAddress(Episode(withImdb: false)));

        // introdb.app stores one numbering per show — the aired one — and cannot
        // convert another order, so a DVD/alternate episode would be answered with a
        // different episode's timestamps.
        Assert.False(client.CanAddress(Episode(mapping: "alternate")));
        Assert.False(client.CanAddress(Episode(orderUnsupported: true)));

        // A series' IMDb id addresses an episode; the item's own id is the fallback
        // when the series has none.
        var seriesOnly = Episode(withImdb: false);
        seriesOnly.ImdbId = "tt1234567";
        Assert.True(client.CanAddress(seriesOnly));
    }

    [Fact]
    public void TheIntroDbStillExpressesNonDefaultOrdersWhenATvdbIdIsPresent()
    {
        using var httpClient = new HttpClient();
        var client = new TheIntroDbClient(httpClient, null!, NullLogger.Instance);

        Assert.True(client.CanAddress(Episode(mapping: "alternate")));
        Assert.False(client.CanAddress(Episode(mapping: "alternate", tvdbId: 0)));
        Assert.False(client.CanAddress(Episode(orderUnsupported: true, mapping: null)));

        var noIds = Episode(withImdb: false, tvdbId: 0);
        noIds.TmdbId = 0;
        Assert.False(client.CanAddress(noIds));
    }

    [Fact]
    public async Task PrimaryAnswerWinsWithoutAskingTheFallback()
    {
        var primary = new FakeSource(IntroDataSource.IntroDbApp, MediaFetchResult.Success(new MediaResponse()));
        var fallback = new FakeSource(IntroDataSource.TheIntroDb, MediaFetchResult.Success(new MediaResponse()));
        var router = CreateRouter(IntroDataSource.IntroDbApp, primary, fallback);

        var outcome = await router.FetchAsync(Episode(), CancellationToken.None);

        Assert.Equal(IntroDataSource.IntroDbApp, outcome.AnsweredBy);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(0, fallback.Calls);
    }

    [Fact]
    public async Task FallbackAnswersWhenThePrimaryHasNoData()
    {
        var primary = new FakeSource(IntroDataSource.IntroDbApp, MediaFetchResult.NotFound());
        var fallback = new FakeSource(IntroDataSource.TheIntroDb, MediaFetchResult.Success(new MediaResponse()));
        var router = CreateRouter(IntroDataSource.IntroDbApp, primary, fallback);

        var outcome = await router.FetchAsync(Episode(), CancellationToken.None);

        Assert.Equal(IntroDataSource.TheIntroDb, outcome.AnsweredBy);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task MissIsOnlyReportedAfterBothSourcesMissed()
    {
        var primary = new FakeSource(IntroDataSource.TheIntroDb, MediaFetchResult.NotFound());
        var fallback = new FakeSource(IntroDataSource.IntroDbApp, MediaFetchResult.NotFound());
        var router = CreateRouter(IntroDataSource.TheIntroDb, primary, fallback);

        var outcome = await router.FetchAsync(Movie("tt0371746"), CancellationToken.None);

        Assert.True(outcome.Result.IsNotFound);
        Assert.Null(outcome.AnsweredBy);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task TransientFailureOutranksAMissSoStoredSegmentsAreKept()
    {
        var primary = new FakeSource(IntroDataSource.TheIntroDb, MediaFetchResult.Error());
        var fallback = new FakeSource(IntroDataSource.IntroDbApp, MediaFetchResult.NotFound());
        var router = CreateRouter(IntroDataSource.TheIntroDb, primary, fallback);

        var outcome = await router.FetchAsync(Movie("tt0371746"), CancellationToken.None);

        // Reporting NotFound here would make Jellyfin delete the segments the plugin
        // already stored for the item.
        Assert.True(outcome.Result.IsError);
        Assert.False(outcome.Result.IsNotFound);
    }

    [Fact]
    public async Task ARateLimitedPrimaryStillLetsTheFallbackAnswer()
    {
        var primary = new FakeSource(IntroDataSource.TheIntroDb, MediaFetchResult.Success(new MediaResponse()), gateActive: true);
        var fallback = new FakeSource(IntroDataSource.IntroDbApp, MediaFetchResult.Success(new MediaResponse()));
        var router = CreateRouter(IntroDataSource.TheIntroDb, primary, fallback);

        var outcome = await router.FetchAsync(Movie("tt0371746"), CancellationToken.None);

        Assert.Equal(IntroDataSource.IntroDbApp, outcome.AnsweredBy);
        Assert.Equal(0, primary.Calls);
        Assert.Equal(1, fallback.Calls);
    }

    [Fact]
    public async Task BothSourcesParkedIsATransientRateLimit()
    {
        var primary = new FakeSource(IntroDataSource.TheIntroDb, MediaFetchResult.Success(new MediaResponse()), gateActive: true);
        var fallback = new FakeSource(IntroDataSource.IntroDbApp, MediaFetchResult.Success(new MediaResponse()), gateActive: true);
        var router = CreateRouter(IntroDataSource.TheIntroDb, primary, fallback);

        var outcome = await router.FetchAsync(Movie("tt0371746"), CancellationToken.None);

        Assert.True(outcome.Result.IsRateLimited);
        Assert.False(outcome.Result.IsNotFound);
    }

    [Fact]
    public void ASourceThatCannotAddressTheItemIsLeftOutOfThePlan()
    {
        var primary = new FakeSource(IntroDataSource.IntroDbApp, MediaFetchResult.NotFound(), addressable: false);
        var fallback = new FakeSource(IntroDataSource.TheIntroDb, MediaFetchResult.Success(new MediaResponse()));
        var router = CreateRouter(IntroDataSource.IntroDbApp, primary, fallback);

        var plan = router.GetResolutionOrder(Episode(withImdb: false));

        Assert.Single(plan);
        Assert.Equal(IntroDataSource.TheIntroDb, plan[0].Source);
    }

    [Fact]
    public void TheConfiguredSourceIsAlwaysAskedFirst()
    {
        var primary = new FakeSource(IntroDataSource.IntroDbApp, MediaFetchResult.NotFound());
        var fallback = new FakeSource(IntroDataSource.TheIntroDb, MediaFetchResult.NotFound());

        var plan = CreateRouter(IntroDataSource.IntroDbApp, primary, fallback).GetResolutionOrder(Movie("tt0371746"));

        Assert.Collection(
            plan,
            first => Assert.Equal(IntroDataSource.IntroDbApp, first.Source),
            second => Assert.Equal(IntroDataSource.TheIntroDb, second.Source));
    }

    [Fact]
    public void NotFoundKeyIsScopedToThePlanSoSwitchingSourcesReChecksTheItem()
    {
        var mediaKey = TheIntroDbSegmentProvider.BuildNotFoundKey(false, 456, 81_189, null, 1, 1, null, null);
        var withIntroDbApp = TheIntroDbSegmentProvider.BuildPlanScopedKey(
            new IIntroSegmentSource[]
            {
                new FakeSource(IntroDataSource.IntroDbApp, MediaFetchResult.NotFound()),
                new FakeSource(IntroDataSource.TheIntroDb, MediaFetchResult.NotFound())
            },
            mediaKey);
        var withTheIntroDb = TheIntroDbSegmentProvider.BuildPlanScopedKey(
            new IIntroSegmentSource[]
            {
                new FakeSource(IntroDataSource.TheIntroDb, MediaFetchResult.NotFound())
            },
            mediaKey);

        Assert.NotEqual(withIntroDbApp, withTheIntroDb);
        Assert.EndsWith(mediaKey, withIntroDbApp, StringComparison.Ordinal);
    }

    private static IntroSegmentRouter CreateRouter(
        IntroDataSource primary,
        params IIntroSegmentSource[] sources)
    {
        var bySource = new Dictionary<IntroDataSource, IIntroSegmentSource>();
        foreach (var source in sources)
        {
            bySource[source.Source] = source;
        }

        return new IntroSegmentRouter(NullLogger.Instance, primary, source => bySource[source]);
    }

    private static IntroLookupRequest Movie(string? imdbId) => new()
    {
        ImdbId = imdbId,
        IsMovie = true
    };

    private static IntroLookupRequest Episode(
        bool withImdb = true,
        string? mapping = null,
        int tvdbId = 81_189,
        bool orderUnsupported = false) => new()
    {
        TvdbId = tvdbId,
        TmdbId = 456,
        ImdbId = withImdb ? "tt0903747" : null,
        SeriesImdbId = withImdb ? "tt0903747" : null,
        IsMovie = false,
        Season = 1,
        Episode = 1,
        Mapping = mapping,
        OrderUnsupported = orderUnsupported
    };

    private sealed class FakeSource : IIntroSegmentSource
    {
        private readonly MediaFetchResult _result;
        private readonly bool _addressable;
        private readonly bool _gateActive;

        public FakeSource(
            IntroDataSource source,
            MediaFetchResult result,
            bool addressable = true,
            bool gateActive = false)
        {
            Source = source;
            _result = result;
            _addressable = addressable;
            _gateActive = gateActive;
        }

        public IntroDataSource Source { get; }

        public int Calls { get; private set; }

        public bool CanAddress(IntroLookupRequest request) => _addressable;

        public bool IsRateLimitActive(IntroLookupRequest request) => _gateActive;

        public Task<MediaFetchResult> FetchAsync(IntroLookupRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_result);
        }
    }
}
