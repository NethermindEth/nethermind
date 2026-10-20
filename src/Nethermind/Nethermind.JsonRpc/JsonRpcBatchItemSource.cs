// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;

namespace Nethermind.JsonRpc;

/// <summary>Enumerates and decodes the items of a JSON-RPC batch, hiding whether it arrived as bytes or as a parsed document.</summary>
/// <remarks>
/// Implementations are mutable structs and are used through a <c>struct</c>-constrained type parameter, so the
/// shared batch loop dispatches without boxing and iterates one instance in place.
/// </remarks>
internal interface IJsonRpcBatchItemSource
{
    /// <summary>The number of items in the batch.</summary>
    int Count { get; }

    /// <summary>Advances to the next batch item and decodes it.</summary>
    /// <param name="request">The decoded request, or <c>null</c> when the item is not a usable JSON-RPC request object.</param>
    /// <param name="decodeException">Why the item could not be decoded, when that is the reason <paramref name="request"/> is <c>null</c>.</param>
    /// <returns><c>false</c> once the batch is exhausted. An undecodable item still returns <c>true</c>, so the loop can answer it with -32600.</returns>
    bool TryGetNext(out JsonRpcRequest? request, out Exception? decodeException);
}

/// <summary>Batch items taken from an already-parsed JSON array.</summary>
/// <remarks>
/// <c>params</c> comes back as a <see cref="JsonElement"/> into the enclosing document, which the caller disposes,
/// so this source never leaves raw params behind.
/// </remarks>
internal struct DocumentBatchItemSource(JsonElement rootElement) : IJsonRpcBatchItemSource
{
    private readonly int _count = rootElement.GetArrayLength();
    private JsonElement.ArrayEnumerator _items = rootElement.EnumerateArray();

    public readonly int Count => _count;

    public bool TryGetNext(out JsonRpcRequest? request, out Exception? decodeException)
    {
        request = null;
        decodeException = null;

        if (!_items.MoveNext())
        {
            return false;
        }

        JsonElement item = _items.Current;
        if (item.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        try
        {
            request = JsonRpcRequestDecoder.CreateRequest(item);
        }
        catch (Exception ex) when (JsonRpcRequestDecoder.IsRequestDecodingException(ex))
        {
            decodeException = ex;
        }

        return true;
    }
}

/// <summary>Batch items sliced straight out of the request bytes, without parsing the array into a document first.</summary>
/// <param name="batchBody">One complete, already validated JSON array.</param>
/// <param name="count">The number of items in <paramref name="batchBody"/>, counted while validating it.</param>
internal struct MemoryBatchItemSource(ReadOnlyMemory<byte> batchBody, int count) : IJsonRpcBatchItemSource
{
    private readonly ReadOnlyMemory<byte> _batchBody = batchBody;
    private readonly int _count = count;
    private JsonReaderState _readerState;
    private int _offset;
    private bool _started;

    public readonly int Count => _count;

    /// <remarks>
    /// An object item is decoded by the same reader that walks the array, so the envelope pass doubles as the skip
    /// over the item. Any other item, or an object that fails to decode, is skipped from its start.
    /// </remarks>
    public bool TryGetNext(out JsonRpcRequest? request, out Exception? decodeException)
    {
        request = null;
        decodeException = null;

        ReadOnlyMemory<byte> unread = _batchBody[_offset..];
        Utf8JsonReader reader = new(unread.Span, isFinalBlock: true, state: _readerState);
        bool hasItem = JsonRpcArrayReader.TryReadItemStart(ref reader, ref _started);
        if (hasItem)
        {
            if (reader.TokenType == JsonTokenType.StartObject)
            {
                try
                {
                    request = JsonRpcRequestDecoder.ReadObjectRequest(unread, ref reader);
                }
                catch (Exception ex) when (JsonRpcRequestDecoder.IsRequestDecodingException(ex))
                {
                    decodeException = ex;
                }
            }

            if (request is null)
            {
                reader.Skip();
            }
        }

        _offset += (int)reader.BytesConsumed;
        _readerState = reader.CurrentState;
        return hasItem;
    }
}
