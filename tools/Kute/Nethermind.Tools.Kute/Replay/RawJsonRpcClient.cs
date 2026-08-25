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
    private const int InitialBufferSize = 16 * 1024;
    private const int MaxBufferSize = 8 * 1024 * 1024;

    private static readonly MediaTypeHeaderValue s_contentType = new(MediaTypeNames.Application.Json)
    {
        CharSet = "utf-8",
    };

    private byte[] _buffer = new byte[InitialBufferSize];

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

            // The body must be drained even when the verdict is already known, or the connection
            // cannot be reused and the next request pays a fresh handshake.
            RequestOutcome outcome = await ClassifyAsync(response, token);

            return response.IsSuccessStatusCode ? outcome : RequestOutcome.HttpError;
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

    /// <summary>
    /// Reads the response, deciding from its top-level members whether it carries a result or an error.
    /// </summary>
    /// <remarks>
    /// A result can run to megabytes while an error is a few hundred bytes, so the response is scanned
    /// incrementally rather than buffered: only enough is parsed to reach the <c>result</c> or
    /// <c>error</c> member, and the rest is drained without being looked at. Buffering a fixed prefix
    /// instead would leave any response longer than that prefix unclassified, and on override-heavy
    /// captures most responses are longer than any sensible prefix.
    /// </remarks>
    private async Task<RequestOutcome> ClassifyAsync(HttpResponseMessage response, CancellationToken token)
    {
        await using Stream stream = await response.Content.ReadAsStreamAsync(token);

        JsonReaderState state = new();
        int buffered = 0;
        bool endOfStream = false;
        RequestOutcome? verdict = null;

        while (verdict is null)
        {
            if (buffered == _buffer.Length && !Grow())
            {
                // A single token larger than the cap: stop parsing but keep the connection usable.
                break;
            }

            int read = endOfStream ? 0 : await stream.ReadAsync(_buffer.AsMemory(buffered), token);
            if (read == 0)
            {
                endOfStream = true;
            }

            buffered += read;
            verdict = Scan(_buffer.AsSpan(0, buffered), endOfStream, ref state, out int consumed);

            if (verdict is null && endOfStream)
            {
                break;
            }

            if (consumed > 0)
            {
                _buffer.AsSpan(consumed, buffered - consumed).CopyTo(_buffer);
                buffered -= consumed;
            }
        }

        await DrainAsync(stream, endOfStream, token);

        // No decisive member means the body was not a JSON-RPC response at all.
        return verdict ?? RequestOutcome.RpcError;
    }

    /// <summary>
    /// Advances the reader over the buffered bytes, looking for a decisive top-level member.
    /// </summary>
    /// <returns>The verdict once one is reachable, otherwise <see langword="null"/> to read more.</returns>
    private static RequestOutcome? Scan(ReadOnlySpan<byte> buffered, bool isFinalBlock, ref JsonReaderState state, out int consumed)
    {
        Utf8JsonReader reader = new(buffered, isFinalBlock, state);
        RequestOutcome? verdict = null;

        try
        {
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.StartArray && reader.CurrentDepth == 0)
                {
                    // A batch response: treat it as a result, since per-entry verdicts are not tracked.
                    verdict = RequestOutcome.Success;
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                {
                    continue;
                }

                if (reader.ValueTextEquals("error"u8))
                {
                    verdict = RequestOutcome.RpcError;
                    break;
                }

                if (reader.ValueTextEquals("result"u8))
                {
                    verdict = RequestOutcome.Success;
                    break;
                }
            }
        }
        catch (JsonException)
        {
            verdict = RequestOutcome.RpcError;
        }

        state = reader.CurrentState;
        consumed = (int)reader.BytesConsumed;

        return verdict;
    }

    private bool Grow()
    {
        if (_buffer.Length >= MaxBufferSize)
        {
            return false;
        }

        Array.Resize(ref _buffer, _buffer.Length * 2);

        return true;
    }

    private async Task DrainAsync(Stream stream, bool endOfStream, CancellationToken token)
    {
        while (!endOfStream && await stream.ReadAsync(_buffer, token) > 0)
        {
        }
    }
}
