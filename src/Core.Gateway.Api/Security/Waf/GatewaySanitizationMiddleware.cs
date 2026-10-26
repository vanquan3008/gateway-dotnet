using System.Diagnostics.Metrics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
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

    [GeneratedRegex(@"(?:\.\.[\\/]|%2e%2e[\\/]|%2e%2e%2f|%2e%2e%5c)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathTraversalPattern();

    [GeneratedRegex(@"(?:<\s*script|javascript\s*:|vbscript\s*:|<\s*iframe)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ScriptInjectionPattern();

    [GeneratedRegex(@"(?:union\s+(?:all\s+)?select|;\s*drop\s+table|waitfor\s+delay|'\s+or\s+(?:'?[0-9]+'?=[0-9]+|true))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlInjectionPattern();

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var path = request.Path.Value ?? string.Empty;
        var query = request.QueryString.Value ?? string.Empty;
        var decodedQuery = Decode(query);

        if (ContainsNullByte(path) || ContainsNullByte(query))
        {
            await BlockAsync(context, "NullByteInjection", "Null byte character detected in request URL.");
            return;
        }

        if (PathTraversalPattern().IsMatch(path) || PathTraversalPattern().IsMatch(decodedQuery))
        {
            await BlockAsync(context, "PathTraversal", "Path traversal pattern detected in request.");
            return;
        }

        if (ScriptInjectionPattern().IsMatch(path) || ScriptInjectionPattern().IsMatch(decodedQuery))
        {
            await BlockAsync(context, "CrossSiteScripting", "Cross-site scripting pattern detected in request.");
            return;
        }

        if (SqlInjectionPattern().IsMatch(query) || SqlInjectionPattern().IsMatch(decodedQuery))
        {
            await BlockAsync(context, "SqlInjection", "SQL injection signature detected in request query.");
            return;
        }

        await next(context);
    }

    private static string Decode(string text)
    {
        var decoded = text.Replace('+', ' ');
        for (var pass = 0; pass < MaxDecodePasses && decoded.Contains('%'); pass++)
        {
            decoded = Uri.UnescapeDataString(decoded);
        }

        return decoded;
    }

    private static bool ContainsNullByte(string text) =>
        text.Contains('\0') || text.Contains("%00", StringComparison.OrdinalIgnoreCase);

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
