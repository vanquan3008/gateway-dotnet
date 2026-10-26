using System.Diagnostics.Metrics;

namespace Core.Gateway.Api.Authentication.Metrics;

internal static class GatewayAuthenticationMetrics
{
    public const string MeterName = "Core.Gateway.Authentication";

    public const string ApiKey = "api_key";
    public const string Signature = "signature";
    public const string Jwt = "jwt";

    /// <summary>
    /// Fixed failure reasons; keep the set small because each one is a metric label value.
    /// </summary>
    public static class Reasons
    {
        public const string Malformed = "malformed";
        public const string Expired = "expired";
        public const string UnknownClient = "unknown_client";
        public const string BodyTooLarge = "body_too_large";
        public const string InvalidSignature = "invalid_signature";
        public const string Replayed = "replayed";
        public const string InvalidKey = "invalid_key";
        public const string InvalidAudience = "invalid_audience";
        public const string InvalidIssuer = "invalid_issuer";
        public const string InvalidToken = "invalid_token";
        public const string IdpUnavailable = "idp_unavailable";
    }

    private const string SchemeTag = "scheme";
    private const string ResultTag = "result";
    private const string ReasonTag = "reason";
    private const string SuccessResult = "success";
    private const string FailureResult = "failure";

    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Attempts = Meter.CreateCounter<long>(
        "gateway.authentication.attempts",
        unit: "{attempt}",
        description: "Authentication attempts by scheme and outcome; failures carry a fixed reason.");

    private static readonly Counter<long> ReplayMemoryFull = Meter.CreateCounter<long>(
        "gateway.authentication.signature.replay_memory_full",
        unit: "{signature}",
        description: "Accepted signatures that could not be remembered locally because the replay memory is full.");

    /// <summary>
    /// Counts an accepted signature that could not be remembered because the replay memory is full.
    /// </summary>
    public static void SignatureReplayMemoryFull() => ReplayMemoryFull.Add(1);

    /// <summary>
    /// Counts a successful authentication.
    /// </summary>
    /// <param name="scheme">The scheme: api_key, signature or jwt.</param>
    public static void Succeeded(string scheme) =>
        Attempts.Add(
            1,
            new KeyValuePair<string, object?>(SchemeTag, scheme),
            new KeyValuePair<string, object?>(ResultTag, SuccessResult));

    /// <summary>
    /// Counts a failed authentication with a fixed reason.
    /// </summary>
    /// <param name="scheme">The scheme: api_key, signature or jwt.</param>
    /// <param name="reason">One of <see cref="Reasons"/>.</param>
    public static void Failed(string scheme, string reason) => Attempts.Add(1,
            new KeyValuePair<string, object?>(SchemeTag, scheme),
            new KeyValuePair<string, object?>(ResultTag, FailureResult),
            new KeyValuePair<string, object?>(ReasonTag, reason)
        );
}
