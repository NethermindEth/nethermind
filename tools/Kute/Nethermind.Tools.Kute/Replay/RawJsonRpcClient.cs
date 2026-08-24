// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Net.Http.Headers;
using System.Net.Mime;
using System.Text.Json;
using Nethermind.Tools.Kute.Auth;

namespace Nethermind.Tools.Kute.Replay;

/// <summary>
/// Sends pre-encoded JSON-RPC request bodies and classifies the responses.
/// </summary>
/// <remarks>
/// Requests are posted straight from their UTF-8 buffer, so a replayed record is never re-encoded.
/// Each instance owns a scratch buffer and is therefore single-threaded: one per replay worker.
/// </remarks>
/// <param name="httpClient">Connection pool shared by the workers of one concurrency level.</param>
/// <param name="uri">Endpoint receiving the requests.</param>
/// <param name="auth">Bearer-token provider, or <see langword="null"/> for an unauthenticated endpoint.</param>
public sealed class RawJsonRpcClient(HttpClient httpClient, Uri uri, IAuth? auth)
{
    /// <summary>
    /// Bytes of a response body retained for classification. A JSON-RPC error response is small, so a
    /// body that overruns this is a large result and is classified as a success without being buffered.
    /// </summary>
    private const int ResponseInspectionLimit = 64 * 1024;

    private static readonly MediaTypeHeaderValue s_contentType = new(MediaTypeNames.Application.Json)
    {
        CharSet = "utf-8",
    };

    // One byte past the limit, so a body that exactly fills the limit is not mistaken for an overrun.
    private readonly byte[] _responseBuffer = new byte[ResponseInspectionLimit + 1];

    /// <summary>Posts one request body and reads its response to completion.</summary>
    /// <param name="body">A JSON-RPC request, as UTF-8 bytes.</param>
    /// <param name="token">Cancels the send.</param>
    /// <returns>How the request ended.</returns>
    public async Task<RequestOutcome> SendAsync(ReadOnlyMemory<byte> body, CancellationToken token)
    {
        try
        {
            ReadOnlyMemoryContent content = new(body);
            content.Headers.ContentType = s_contentType;

            using HttpRequestMessage request = new(HttpMethod.Post, uri) { Content = content };
            if (auth is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.AuthToken);
            }

            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);

            // The body must be drained even when it is not inspected, or the connection cannot be reused.
            int inspected = await ReadAndDrainAsync(response, token);

            if (!response.IsSuccessStatusCode)
            {
                return RequestOutcome.HttpError;
            }

            return inspected >= 0 && HasRpcError(_responseBuffer.AsSpan(0, inspected))
                ? RequestOutcome.RpcError
                : RequestOutcome.Success;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException)
        {
            return RequestOutcome.TransportError;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            // HttpClient surfaces its own timeout as a cancellation unrelated to the caller's token.
            return RequestOutcome.TransportError;
        }
    }

    /// <summary>Buffers the leading bytes of a response and drains the remainder.</summary>
    /// <returns>Bytes buffered, or <c>-1</c> if the body overran the inspection limit.</returns>
    private async Task<int> ReadAndDrainAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(token);

        int filled = 0;
        while (filled < _responseBuffer.Length)
        {
            int read = await stream.ReadAsync(_responseBuffer.AsMemory(filled), token);
            if (read == 0)
            {
                return filled;
            }

            filled += read;
        }

        // Overran the limit: finish reading so the connection stays reusable, then report truncation.
        while (await stream.ReadAsync(_responseBuffer, token) > 0)
        {
        }

        return -1;
    }

    /// <summary>Reports whether a JSON-RPC response object carries a top-level <c>error</c> member.</summary>
    private static bool HasRpcError(ReadOnlySpan<byte> response)
    {
        try
        {
            Utf8JsonReader reader = new(response);
            if (!reader.Read())
            {
                return true;
            }

            if (reader.TokenType == JsonTokenType.StartArray)
            {
                // A batch response is an error only if one of its entries is.
                while (reader.Read())
                {
                    if (reader.TokenType == JsonTokenType.StartObject && ObjectHasError(ref reader))
                    {
                        return true;
                    }
                }

                return false;
            }

            return reader.TokenType != JsonTokenType.StartObject || ObjectHasError(ref reader);
        }
        catch (JsonException)
        {
            return true;
        }

        static bool ObjectHasError(ref Utf8JsonReader reader)
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isError = reader.ValueTextEquals("error"u8);
                if (!reader.Read())
                {
                    return true;
                }

                if (isError)
                {
                    return reader.TokenType != JsonTokenType.Null;
                }

                reader.Skip();
            }

            return false;
        }
    }
}
