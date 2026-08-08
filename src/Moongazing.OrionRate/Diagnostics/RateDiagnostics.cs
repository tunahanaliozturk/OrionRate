namespace Moongazing.OrionRate.Diagnostics;

using System.Diagnostics.Metrics;

using Moongazing.Orion.Abstractions.Diagnostics;

/// <summary>
/// OpenTelemetry instrumentation for the rate limiter. Built on the Orion family's
/// <see cref="OrionInstrumentation"/> spine, so it shares the family's naming and static-tag
/// conventions: a <see cref="Meter"/> named <c>Moongazing.OrionRate</c> (subscribe by that name)
/// carrying <c>orion.rate.allowed</c> / <c>orion.rate.throttled</c> (decisions) and
/// <c>orion.rate.remaining</c> (a histogram of permits left at each decision), each tagged with the
/// low-cardinality policy name. Keys are deliberately NOT tagged (unbounded cardinality). Multi-tenant
/// / multi-region labels configured through <see cref="OrionInstrumentation.SetStaticTags"/> are stamped
/// onto every measurement.
/// <para>A process-wide <see cref="Shared"/> instance makes telemetry emit by default.</para>
/// </summary>
public sealed class RateDiagnostics : OrionInstrumentation
{
    /// <summary>The meter name OpenTelemetry consumers subscribe to.</summary>
    public const string MeterName = "Moongazing.OrionRate";

    /// <summary>The tag key carrying the policy name on every measurement.</summary>
    public const string PolicyTagKey = "orion.rate.policy";

    private static readonly System.Lazy<RateDiagnostics> SharedInstance =
        new(static () => new RateDiagnostics());

    /// <summary>Create the meter and its instruments.</summary>
    public RateDiagnostics()
        : base(OrionTelemetry.ScopeName("OrionRate"), MeterVersion.Value)
    {
        Allowed = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("rate", "allowed"),
            unit: "{request}",
            description: "Requests admitted by the limiter, tagged with the policy.");

        Throttled = Meter.CreateCounter<long>(
            OrionTelemetry.MetricName("rate", "throttled"),
            unit: "{request}",
            description: "Requests rejected by the limiter, tagged with the policy.");

        Remaining = Meter.CreateHistogram<long>(
            OrionTelemetry.MetricName("rate", "remaining"),
            unit: "{permit}",
            description: "Permits remaining at each decision, tagged with the policy.");
    }

    /// <summary>The process-wide default instance, so telemetry emits without explicit wiring.</summary>
    public static RateDiagnostics Shared => SharedInstance.Value;

    /// <summary>Counts admitted requests.</summary>
    public Counter<long> Allowed { get; }

    /// <summary>Counts throttled requests.</summary>
    public Counter<long> Throttled { get; }

    /// <summary>Records permits remaining at each decision.</summary>
    public Histogram<long> Remaining { get; }

    /// <summary>Record one decision for <paramref name="policy"/>.</summary>
    /// <param name="policy">The policy name (low cardinality).</param>
    /// <param name="result">The decision.</param>
    public void Record(string policy, RateResult result)
    {
        var tag = new System.Collections.Generic.KeyValuePair<string, object?>(PolicyTagKey, policy);
        var tags = Tag(tag);
        if (result.Allowed)
        {
            Allowed.Add(1, tags);
        }
        else
        {
            Throttled.Add(1, tags);
        }
        Remaining.Record(result.Remaining, tags);
    }
}
