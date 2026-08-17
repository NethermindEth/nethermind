// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Net;
using System.Net.Http.Headers;

namespace Nethermind.Init.Snapshot;

internal sealed record SnapshotRemoteInfo(long? Length, EntityTagHeaderValue? ETag, bool SupportsRanges);

internal sealed class SnapshotHttpClient : IDisposable
{
    private const int MaxRedirects = 10;
    private const int SkipBufferSize = 65536;

    private readonly HttpClient _httpClient = new(new HttpClientHandler { AllowAutoRedirect = false });

    public async Task<SnapshotRemoteInfo> ProbeAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await GetAsync(url, new RangeHeaderValue(0, 0), ifRange: null, cancellationToken).ConfigureAwait(false);
        EntityTagHeaderValue? etag = response.Headers.ETag;

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            long? length = response.Content.Headers.ContentRange?.Length;
            return new SnapshotRemoteInfo(length, etag, length is not null);
        }

        return new SnapshotRemoteInfo(response.Content.Headers.ContentLength, etag, false);
    }

    public async Task<HttpResponseMessage> GetAsync(
        string url, RangeHeaderValue? range, EntityTagHeaderValue? ifRange, CancellationToken cancellationToken)
    {
        Uri currentUri = new(url);

        for (int redirects = 0; redirects < MaxRedirects; redirects++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, currentUri);
            if (range is not null)
                request.Headers.Range = range;
            if (ifRange is not null && !ifRange.IsWeak)
                request.Headers.IfRange = new RangeConditionHeaderValue(ifRange);

            HttpResponseMessage response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.MovedPermanently
                    or HttpStatusCode.Found
                    or HttpStatusCode.SeeOther
                    or HttpStatusCode.TemporaryRedirect
                    or HttpStatusCode.PermanentRedirect:
                    {
                        Uri? location = response.Headers.Location;
                        response.Dispose();
                        if (location is null)
                            throw new IOException("Redirect response missing Location header.");
                        currentUri = new Uri(currentUri, location);
                        continue;
                    }
                case HttpStatusCode.RequestedRangeNotSatisfiable:
                    return response;
                default:
                    return response.EnsureSuccessStatusCode();
            }
        }

        throw new IOException("Too many redirects while downloading snapshot.");
    }

    public void Dispose() => _httpClient.Dispose();

    public static bool IsPermanentHttpError(HttpRequestException e) =>
        e.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError
            and not HttpStatusCode.TooManyRequests
            and not HttpStatusCode.RequestedRangeNotSatisfiable;

    public static async Task SkipAsync(Stream content, long bytesToSkip, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(SkipBufferSize);
        try
        {
            long remaining = bytesToSkip;
            while (remaining > 0)
            {
                int chunk = (int)Math.Min(SkipBufferSize, remaining);
                await content.ReadAtLeastAsync(buffer.AsMemory(0, chunk), chunk, throwOnEndOfStream: true, cancellationToken).ConfigureAwait(false);
                remaining -= chunk;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
