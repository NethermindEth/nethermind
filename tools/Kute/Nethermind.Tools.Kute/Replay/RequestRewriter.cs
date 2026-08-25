// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Text.Json;

namespace Nethermind.Tools.Kute.Replay;

/// <summary>One byte range of a captured request to replace or remove.</summary>
/// <param name="Start">Index of the first byte affected.</param>
/// <param name="Length">Number of bytes affected.</param>
/// <param name="IsBlockParameter">
/// <see langword="true"/> when the range is the block parameter and is replaced by the target tag;
/// <see langword="false"/> when the range is removed outright.
/// </param>
public readonly record struct RequestEdit(int Start, int Length, bool IsBlockParameter);

/// <summary>
/// Adjusts a captured JSON-RPC request so it can be replayed against a node's current head.
/// </summary>
/// <remarks>
/// Two things in a capture go stale the moment it is replayed at a different height: the block
/// parameter names a block the node may no longer hold, and any fee field was priced against the
/// base fee at capture time. Both are fixed here in one pass.
/// <para>
/// Works on the raw UTF-8 bytes rather than a parsed document. Captured <c>eth_call</c> records run
/// to hundreds of kilobytes, almost all of it the state-override map in the last parameter, so
/// parsing and writing back every record would cost more than the node spends answering it. The scan
/// stops once the block parameter has been located, so the override map is never read.
/// </para>
/// </remarks>
public static class RequestRewriter
{
    /// <summary>Largest number of edits a single request can need: three fee fields and the block parameter.</summary>
    public const int MaxEdits = 4;

    /// <summary>
    /// Zero-based index of the block parameter for each method whose block position is known.
    /// </summary>
    /// <remarks>
    /// The position is per-method, not a convention: <c>eth_call</c> and <c>eth_getBalance</c> carry it
    /// second, while <c>eth_getStorageAt</c>, <c>eth_getProof</c> and <c>trace_call</c> carry it third.
    /// Rewriting the wrong slot would replace a storage key or a trace-type list and leave the stale
    /// block in place, so a method that is not listed is replayed untouched rather than guessed at.
    /// </remarks>
    private static int BlockParameterIndex(ReadOnlySpan<byte> method) => method switch
    {
        _ when method.SequenceEqual("eth_call"u8) => 1,
        _ when method.SequenceEqual("eth_estimateGas"u8) => 1,
        _ when method.SequenceEqual("eth_createAccessList"u8) => 1,
        _ when method.SequenceEqual("eth_getBalance"u8) => 1,
        _ when method.SequenceEqual("eth_getCode"u8) => 1,
        _ when method.SequenceEqual("eth_getTransactionCount"u8) => 1,
        _ when method.SequenceEqual("eth_getBlockByNumber"u8) => 0,
        _ when method.SequenceEqual("eth_simulateV1"u8) => 1,
        _ when method.SequenceEqual("eth_getStorageAt"u8) => 2,
        _ when method.SequenceEqual("eth_getProof"u8) => 2,
        _ when method.SequenceEqual("trace_call"u8) => 2,
        _ => -1,
    };

    /// <summary>
    /// Works out the edits a captured request needs, in ascending, non-overlapping order.
    /// </summary>
    /// <param name="request">A single JSON-RPC request, as UTF-8 bytes.</param>
    /// <param name="forceBlockParameter">Whether the block parameter should be replaced.</param>
    /// <param name="stripFeeFields">Whether fee fields should be removed from the call object.</param>
    /// <param name="edits">Receives the planned edits; must hold at least <see cref="MaxEdits"/>.</param>
    /// <returns>Number of edits written to <paramref name="edits"/>; <c>0</c> to replay verbatim.</returns>
    public static int Plan(ReadOnlySpan<byte> request, bool forceBlockParameter, bool stripFeeFields, Span<RequestEdit> edits)
    {
        if (edits.Length < MaxEdits)
        {
            throw new ArgumentException($"Needs room for {MaxEdits} edits.", nameof(edits));
        }

        try
        {
            int blockIndex = forceBlockParameter ? FindBlockParameterIndex(request) : -1;
            if (blockIndex < 0 && !stripFeeFields)
            {
                return 0;
            }

            return PlanParams(request, blockIndex, stripFeeFields, edits);
        }
        catch (JsonException)
        {
            // A malformed record is replayed as captured; the node's response classifies it.
            return 0;
        }
    }

    /// <summary>
    /// Reads the request's <c>method</c> and maps it to the position of its block parameter.
    /// </summary>
    /// <returns>The zero-based parameter index, or <c>-1</c> if the method is unknown or absent.</returns>
    /// <remarks>
    /// Stops as soon as <c>method</c> is found. Recorders put it before <c>params</c>, so this normally
    /// reads only the first few bytes; the reverse order costs a walk over <c>params</c> instead.
    /// </remarks>
    private static int FindBlockParameterIndex(ReadOnlySpan<byte> request)
    {
        Utf8JsonReader reader = new(request);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return -1;
        }

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isMethod = reader.ValueTextEquals("method"u8);
            if (!reader.Read())
            {
                return -1;
            }

            if (isMethod)
            {
                return reader.TokenType == JsonTokenType.String && !reader.ValueIsEscaped
                    ? BlockParameterIndex(reader.ValueSpan)
                    : -1;
            }

            reader.Skip();
        }

        return -1;
    }

    /// <summary>Walks the <c>params</c> array, planning the fee removals and the block replacement.</summary>
    private static int PlanParams(ReadOnlySpan<byte> request, int blockIndex, bool stripFeeFields, Span<RequestEdit> edits)
    {
        Utf8JsonReader reader = new(request);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return 0;
        }

        int count = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            bool isParams = reader.ValueTextEquals("params"u8);
            if (!reader.Read())
            {
                return count;
            }

            if (!isParams)
            {
                reader.Skip();
                continue;
            }

            if (reader.TokenType != JsonTokenType.StartArray)
            {
                return count;
            }

            for (int index = 0; ; index++)
            {
                if (!reader.Read() || reader.TokenType == JsonTokenType.EndArray)
                {
                    return count;
                }

                if (index == blockIndex)
                {
                    int tokenStart = (int)reader.TokenStartIndex;
                    reader.Skip();
                    edits[count++] = new RequestEdit(tokenStart, (int)reader.BytesConsumed - tokenStart, true);

                    return count;
                }

                if (index == 0 && stripFeeFields && reader.TokenType == JsonTokenType.StartObject)
                {
                    count += PlanFeeRemovals(ref reader, edits[count..]);
                }
                else
                {
                    reader.Skip();
                }

                // Nothing further to plan, and walking on would tokenise the state-override map.
                if (blockIndex < 0)
                {
                    return count;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Plans the removal of every fee field in the call object the reader is positioned on, leaving the
    /// reader on that object's closing brace.
    /// </summary>
    /// <remarks>
    /// A removal has to take one comma with it or the result is not valid JSON. While nothing has
    /// survived yet, a removal takes the comma that follows it; once some property has been kept, a
    /// removal takes the comma before it instead. Clamping the backward reach to the end of the previous
    /// removal keeps a run of adjacent fee fields from planning overlapping ranges.
    /// </remarks>
    private static int PlanFeeRemovals(ref Utf8JsonReader reader, Span<RequestEdit> edits)
    {
        int count = 0;
        int previousValueEnd = (int)reader.BytesConsumed;
        int lastRemovalEnd = 0;
        bool noSurvivorYet = true;

        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            int nameStart = (int)reader.TokenStartIndex;
            bool isFee = reader.ValueTextEquals("gasPrice"u8)
                || reader.ValueTextEquals("maxFeePerGas"u8)
                || reader.ValueTextEquals("maxPriorityFeePerGas"u8);

            if (!reader.Read())
            {
                return count;
            }

            reader.Skip();
            int valueEnd = (int)reader.BytesConsumed;

            if (!isFee || count == edits.Length)
            {
                previousValueEnd = valueEnd;
                noSurvivorYet = false;
                continue;
            }

            int removalStart;
            int removalEnd;
            if (noSurvivorYet)
            {
                Utf8JsonReader lookahead = reader;
                removalStart = nameStart;
                removalEnd = lookahead.Read() && lookahead.TokenType == JsonTokenType.PropertyName
                    ? (int)lookahead.TokenStartIndex
                    : valueEnd;
            }
            else
            {
                removalStart = Math.Max(previousValueEnd, lastRemovalEnd);
                removalEnd = valueEnd;
            }

            edits[count++] = new RequestEdit(removalStart, removalEnd - removalStart, false);
            lastRemovalEnd = removalEnd;
        }

        return count;
    }

    /// <summary>Length a request will occupy once <paramref name="edits"/> are applied.</summary>
    public static int RewrittenLength(ReadOnlySpan<byte> request, ReadOnlySpan<RequestEdit> edits, ReadOnlySpan<byte> quotedTag)
    {
        int length = request.Length;
        foreach (RequestEdit edit in edits)
        {
            length -= edit.Length;
            if (edit.IsBlockParameter)
            {
                length += quotedTag.Length;
            }
        }

        return length;
    }

    /// <summary>
    /// Writes <paramref name="request"/> to <paramref name="destination"/> with the planned edits applied.
    /// </summary>
    /// <param name="request">A single JSON-RPC request, as UTF-8 bytes.</param>
    /// <param name="edits">Edits from <see cref="Plan"/>, ascending and non-overlapping.</param>
    /// <param name="quotedTag">Replacement block parameter, including its JSON quoting.</param>
    /// <param name="destination">Buffer receiving the rewritten request.</param>
    /// <returns>Bytes written, or <c>-1</c> if <paramref name="destination"/> is too small.</returns>
    public static int Apply(ReadOnlySpan<byte> request, ReadOnlySpan<RequestEdit> edits, ReadOnlySpan<byte> quotedTag, Span<byte> destination)
    {
        if (destination.Length < RewrittenLength(request, edits, quotedTag))
        {
            return -1;
        }

        int read = 0;
        int written = 0;

        foreach (RequestEdit edit in edits)
        {
            ReadOnlySpan<byte> kept = request[read..edit.Start];
            kept.CopyTo(destination[written..]);
            written += kept.Length;

            if (edit.IsBlockParameter)
            {
                quotedTag.CopyTo(destination[written..]);
                written += quotedTag.Length;
            }

            read = edit.Start + edit.Length;
        }

        ReadOnlySpan<byte> tail = request[read..];
        tail.CopyTo(destination[written..]);

        return written + tail.Length;
    }

    /// <summary>
    /// Locates the second entry of the request's <c>params</c> array, which by convention carries the
    /// block number, hash or tag for state-reading methods.
    /// </summary>
    /// <param name="request">A single JSON-RPC request, as UTF-8 bytes.</param>
    /// <param name="start">Index of the first byte of the block parameter.</param>
    /// <param name="length">Length of the block parameter in bytes.</param>
    /// <returns>
    /// <see langword="true"/> if the request is an object with a <c>params</c> array holding at least
    /// two entries; otherwise <see langword="false"/>.
    /// </returns>
    public static bool TryLocateBlockParameter(ReadOnlySpan<byte> request, out int start, out int length)
    {
        Span<RequestEdit> edits = stackalloc RequestEdit[MaxEdits];
        int count = Plan(request, forceBlockParameter: true, stripFeeFields: false, edits);

        for (int i = 0; i < count; i++)
        {
            if (edits[i].IsBlockParameter)
            {
                start = edits[i].Start;
                length = edits[i].Length;
                return true;
            }
        }

        start = 0;
        length = 0;
        return false;
    }

    /// <summary>Reports whether the call object of a request still carries a fee field.</summary>
    /// <param name="request">A single JSON-RPC request, as UTF-8 bytes.</param>
    public static bool HasFeeField(ReadOnlySpan<byte> request)
    {
        Span<RequestEdit> edits = stackalloc RequestEdit[MaxEdits];

        return Plan(request, forceBlockParameter: false, stripFeeFields: true, edits) > 0;
    }
}
