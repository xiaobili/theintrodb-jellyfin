using System.Text.Json.Serialization;

namespace TheIntroDB.Api;

/// <summary>
/// One timestamp block of a <see cref="IntroDbAppSegmentsResponse"/>. introdb.app
/// sends the offset twice, in seconds and in milliseconds; the millisecond fields
/// win when present, so a fractional second never loses precision.
/// </summary>
public sealed class IntroDbAppSegment
{
    /// <summary>
    /// Gets or sets the start offset in seconds.
    /// </summary>
    [JsonPropertyName("start_sec")]
    public double? StartSec { get; set; }

    /// <summary>
    /// Gets or sets the end offset in seconds.
    /// </summary>
    [JsonPropertyName("end_sec")]
    public double? EndSec { get; set; }

    /// <summary>
    /// Gets or sets the start offset in milliseconds.
    /// </summary>
    [JsonPropertyName("start_ms")]
    public long? StartMs { get; set; }

    /// <summary>
    /// Gets or sets the end offset in milliseconds.
    /// </summary>
    [JsonPropertyName("end_ms")]
    public long? EndMs { get; set; }

    /// <summary>
    /// Gets or sets the community confidence in this timestamp, in [0, 1].
    /// </summary>
    [JsonPropertyName("confidence")]
    public double? Confidence { get; set; }

    /// <summary>
    /// Gets or sets how many submissions back this timestamp.
    /// </summary>
    [JsonPropertyName("submission_count")]
    public int? SubmissionCount { get; set; }
}
