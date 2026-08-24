// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.Replay;

/// <summary>
/// Replaces the block parameter of a captured JSON-RPC request so a trace recorded against
/// historical blocks can be replayed against a node's current head.
/// </summary>
/// <remarks>
/// Works on the raw UTF-8 bytes rather than a parsed document. Captured <c>eth_call</c> records run
/// to hundreds of kilobytes, almost all of it the state-override map in the last parameter, so
/// parsing and writing back every record would cost more than the node spends answering it.
/// The scan stops as soon as the block parameter has been located, so the override map is never
/// read, and a record whose tag already matches the target needs no copy at all.
/// </remarks>
public static class BlockTagRewriter
{
    /// <summary>
    /// Locates the second entry of the request's <c>params</c> array, which by convention carries the
    /// block number, hash or tag for state-reading methods.
    /// </summary>
    /// <param name="request">A single JSON-RPC request, as UTF-8 bytes.</param>
    /// <param name="start">Index of the first byte of the block parameter.</param>
    /// <param name="length">Length of the block parameter in bytes.</param>
    /// <returns>
    /// <see langword="true"/> if the request is an object with a <c>params</c> array holding at least
    /// two entries; otherwise <see langword="false"/>, leaving the request to be replayed verbatim.
    /// </returns>
    public static bool TryLocateBlockParameter(ReadOnlySpan<byte> request, out int start, out int length)
    {
        start = 0;
        length = 0;

        try
        {
            Utf8JsonReader reader = new(request);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                bool isParams = reader.ValueTextEquals("params"u8);
                if (!reader.Read())
                {
                    return false;
                }

                if (!isParams)
                {
                    reader.Skip();
                    continue;
                }

                if (reader.TokenType != JsonTokenType.StartArray)
                {
                    return false;
                }

                // Step over the first entry, then measure the second.
                if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
                {
                    return false;
                }

                reader.Skip();

                if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
                {
                    return false;
                }

                int tokenStart = (int)reader.TokenStartIndex;
                reader.Skip();

                start = tokenStart;
                length = (int)reader.BytesConsumed - tokenStart;
                return true;
            }
        }
        catch (JsonException)
        {
            // A malformed record is replayed as captured; the node's response classifies it.
            return false;
        }

        return false;
    }

    /// <summary>
    /// Rewrites the block parameter of <paramref name="request"/> to <paramref name="quotedTag"/>.
    /// </summary>
    /// <param name="request">A single JSON-RPC request, as UTF-8 bytes.</param>
    /// <param name="start">Index of the block parameter, from <see cref="TryLocateBlockParameter"/>.</param>
    /// <param name="length">Length of the block parameter, from <see cref="TryLocateBlockParameter"/>.</param>
    /// <param name="quotedTag">The replacement parameter, including its JSON quoting.</param>
    /// <param name="destination">Buffer receiving the rewritten request.</param>
    /// <returns>
    /// Bytes written to <paramref name="destination"/>, or <c>-1</c> if it is too small.
    /// </returns>
    public static int Rewrite(ReadOnlySpan<byte> request, int start, int length, ReadOnlySpan<byte> quotedTag, Span<byte> destination)
    {
        int written = request.Length - length + quotedTag.Length;
        if (destination.Length < written)
        {
            return -1;
        }

        request[..start].CopyTo(destination);
        quotedTag.CopyTo(destination[start..]);
        request[(start + length)..].CopyTo(destination[(start + quotedTag.Length)..]);

        return written;
    }
}
