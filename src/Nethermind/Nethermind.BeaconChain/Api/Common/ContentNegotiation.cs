// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Globalization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;

namespace Nethermind.BeaconChain.Api.Common;

/// <summary>RFC 9110 section 12.5.1 <c>Accept</c> negotiation between the beacon-api's two wire formats.</summary>
internal static class ContentNegotiation
{
    public const string Json = "application/json";
    public const string OctetStream = "application/octet-stream";

    public enum ResponseFormat { Json, Ssz }

    /// <summary>
    /// Picks a response representation from the request's <c>Accept</c> header.
    /// </summary>
    /// <param name="sszSupported">Whether the endpoint can serve <see cref="OctetStream"/> at all.</param>
    /// <returns><c>null</c> when nothing offered is acceptable - the caller must answer 406.</returns>
    public static ResponseFormat? Negotiate(HttpContext ctx, bool sszSupported)
    {
        StringValues acceptValues = ctx.Request.Headers.Accept;
        if (acceptValues.Count == 0)
        {
            // No header: any representation is acceptable (RFC 9110 section 12.5.1); JSON is the default.
            return ResponseFormat.Json;
        }

        double bestJsonQ = -1, bestSszQ = -1;
        foreach (string? raw in acceptValues)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;
            foreach (Range r in raw.AsSpan().Split(','))
            {
                ReadOnlySpan<char> range = raw.AsSpan()[r].Trim();
                if (range.IsEmpty) continue;

                int semicolon = range.IndexOf(';');
                ReadOnlySpan<char> mediaType = (semicolon < 0 ? range : range[..semicolon]).TrimEnd();
                double q = semicolon < 0 ? 1.0 : ParseQuality(range[(semicolon + 1)..]);
                if (q <= 0) continue;

                bool wildcard = mediaType.Equals("*/*", StringComparison.Ordinal)
                    || mediaType.Equals("application/*", StringComparison.OrdinalIgnoreCase);
                if (wildcard || mediaType.Equals(Json, StringComparison.OrdinalIgnoreCase))
                {
                    if (q > bestJsonQ) bestJsonQ = q;
                }

                if (sszSupported && (wildcard || mediaType.Equals(OctetStream, StringComparison.OrdinalIgnoreCase)))
                {
                    if (q > bestSszQ) bestSszQ = q;
                }
            }
        }

        if (bestJsonQ < 0 && bestSszQ < 0) return null;
        // A strict SSZ preference wins; JSON is the default representation on a tie (including when
        // only a wildcard matched both).
        return bestSszQ > bestJsonQ ? ResponseFormat.Ssz : ResponseFormat.Json;
    }

    private static double ParseQuality(ReadOnlySpan<char> parameters)
    {
        foreach (Range r in parameters.Split(';'))
        {
            ReadOnlySpan<char> parameter = parameters[r].Trim();
            if (parameter.StartsWith("q=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(parameter[2..], NumberStyles.Float, CultureInfo.InvariantCulture, out double q))
            {
                return q;
            }
        }

        return 1.0;
    }

    public static Task WriteNotAcceptable(HttpContext ctx) =>
        ApiErrors.Write(ctx, StatusCodes.Status406NotAcceptable,
            "No representation acceptable to the request's Accept header is available", ctx.RequestAborted);

    /// <summary>Validates a request's <c>Content-Type</c> against the media types an endpoint accepts.</summary>
    public static bool IsAcceptableContentType(HttpContext ctx, params ReadOnlySpan<string> accepted)
    {
        string? contentType = ctx.Request.ContentType;
        if (string.IsNullOrEmpty(contentType)) return false;

        int semicolon = contentType.IndexOf(';');
        ReadOnlySpan<char> mediaType = (semicolon < 0 ? contentType.AsSpan() : contentType.AsSpan(0, semicolon)).Trim();
        foreach (string candidate in accepted)
        {
            if (mediaType.Equals(candidate, StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }

    public static Task WriteUnsupportedMediaType(HttpContext ctx, string expected) =>
        ApiErrors.Write(ctx, StatusCodes.Status415UnsupportedMediaType,
            $"Request Content-Type must be '{expected}'", ctx.RequestAborted);
}
