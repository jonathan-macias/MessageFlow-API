using System.Diagnostics.Metrics;

namespace MessageFlow.Infrastructure.AI;

/// <summary>
/// Counters for text-generation calls, exported with the rest of the OpenTelemetry metrics.
/// Labels carry only provider names and outcomes, never prompts, keys or user data.
/// </summary>
public static class AiMetrics
{
    public const string MeterName = "MessageFlow.AI";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Requests = Meter.CreateCounter<long>(
        "messageflow_ai_requests_total",
        description: "Text-generation attempts by provider and outcome.");

    private static readonly Counter<long> Fallbacks = Meter.CreateCounter<long>(
        "messageflow_ai_fallbacks_total",
        description: "Times the fallback provider took over from the primary.");

    public static void RecordRequest(string provider, string outcome)
        => Requests.Add(1, new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("outcome", outcome));

    public static void RecordFallback(string from, string to)
        => Fallbacks.Add(1, new KeyValuePair<string, object?>("from", from),
            new KeyValuePair<string, object?>("to", to));
}
