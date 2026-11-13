using System.Diagnostics.Metrics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Core.Gateway.Api.Configuration;
using Microsoft.AspNetCore.Mvc;

namespace Core.Gateway.Api.Security.Waf;

/// <summary>
/// Lightweight edge sanitization middleware inspecting request paths and query strings for common exploit patterns.
/// </summary>
public sealed partial class GatewaySanitizationMiddleware(
    RequestDelegate next,
    ILogger<GatewaySanitizationMiddleware> logger)
{
    public const string MeterName = "Core.Gateway.Security";

    private const int MaxDecodePasses = 2;

    private static readonly Meter Meter = new(MeterName);
    private static readonly Counter<long> BlockedRequests = Meter.CreateCounter<long>(
        "gateway.security.waf.blocked_total",
        unit: "{request}",
        description: "Number of suspicious requests blocked by gateway sanitization.");

    [GeneratedRegex(@"\.\.[\\/]", RegexOptions.CultureInvariant)]
    private static partial Regex PathTraversalPattern();

    [GeneratedRegex(@"(?:<\s*script|javascript\s*:|vbscript\s*:|<\s*iframe)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptInjectionPattern();

    [GeneratedRegex(@"(?:union\s+(?:all\s+)?select|;\s*drop\s+table|waitfor\s+delay|'\s+or\s+(?:'?[0-9]+'?=[0-9]+|true))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlInjectionPattern();

    /// <summary>
    /// Blocks null bytes and path traversal; XSS and SQL unless the route sets InjectionFilter to false.
    /// </summary>
    /// <param name="context">The current request.</param>
    /// <returns>A task that completes when the request was forwarded or blocked.</returns>
    public Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var path = Decode(request.Path.Value ?? string.Empty, plusIsSpace: false);
        var query = Decode(request.QueryString.Value ?? string.Empty, plusIsSpace: true);

        if (path.Contains('\0') || query.Contains('\0'))
        {
            return BlockAsync(context, "NullByteInjection", "Null byte character detected in request URL.");
        }

        if (PathTraversalPattern().IsMatch(path) || PathTraversalPattern().IsMatch(query))
        {
            return BlockAsync(context, "PathTraversal", "Path traversal pattern detected in request.");
        }

        if (!InjectionFilterEnabled(context))
        {
            return next(context);
        }

        if (ScriptInjectionPattern().IsMatch(path) || ScriptInjectionPattern().IsMatch(query))
        {
            return BlockAsync(context, "CrossSiteScripting", "Cross-site scripting pattern detected in request.");
        }

        if (SqlInjectionPattern().IsMatch(query))
        {
            return BlockAsync(context, "SqlInjection", "SQL injection signature detected in request query.");
        }

        return next(context);
    }

    /// <summary>
    /// Percent-decodes up to two passes to catch double encoding.
    /// </summary>
    private static string Decode(string text, bool plusIsSpace)
    {
        var decoded = plusIsSpace ? text.Replace('+', ' ') : text;
        for (var pass = 0; pass < MaxDecodePasses && decoded.Contains('%'); pass++)
        {
            decoded = Uri.UnescapeDataString(decoded);
        }

        return decoded;
    }

    /// <summary>
    /// Whether the matched route keeps the injection filter on.
    /// </summary>
    private static bool InjectionFilterEnabled(HttpContext context) =>
        context.GetRouteMetadata(GatewayRouteMetadata.InjectionFilter) is not { } value ||
        !bool.TryParse(value, out var enabled) ||
        enabled;

    private async Task BlockAsync(HttpContext context, string category, string detail)
    {
        BlockedRequests.Add(1, new KeyValuePair<string, object?>("category", category));
        logger.LogWarning(
            "Request blocked by sanitization filter. Category: {Category}, Path: {Path}, IP: {IP}",
            category,
            context.Request.Path,
            context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/problem+json";

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Bad Request",
            Detail = detail,
            Type = "https://tools.ietf.org/html/rfc9110#section-15.5.1"
        };
        problem.Extensions["code"] = "SECURITY_POLICY_VIOLATION";

        await JsonSerializer.SerializeAsync(context.Response.Body, problem, cancellationToken: context.RequestAborted);
    }
}
