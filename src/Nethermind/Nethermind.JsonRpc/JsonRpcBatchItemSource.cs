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
    /// <summary>The item count if the source already knows it, otherwise <c>null</c> - see <see cref="ScanCount"/>.</summary>
    int? KnownCount { get; }

    /// <summary>Counts the items by scanning the batch. Only worth calling when <see cref="KnownCount"/> is <c>null</c> and the count is actually needed.</summary>
    int ScanCount();

    /// <summary>Advances to the next batch item and decodes it.</summary>
    /// <param name="request">The decoded request, or <c>null</c> when the item is not a usable JSON-RPC request object.</param>
    /// <param name="ownedDocument">A document whose lifetime the caller must extend to the end of the batch, or <c>null</c>.</param>
    /// <param name="decodeException">Why the item could not be decoded, when that is the reason <paramref name="request"/> is <c>null</c>.</param>
    /// <returns><c>false</c> once the batch is exhausted. An undecodable item still returns <c>true</c>, so the loop can answer it with -32600.</returns>
    bool TryGetNext(out JsonRpcRequest? request, out JsonDocument? ownedDocument, out Exception? decodeException);
}

/// <summary>Batch items taken from an already-parsed JSON array.</summary>
/// <remarks>
/// <c>params</c> comes back as a <see cref="JsonElement"/> into the enclosing document, which the caller disposes,
/// so this source never hands out an owned document and never leaves raw params behind.
/// </remarks>
internal struct DocumentBatchItemSource(JsonElement rootElement) : IJsonRpcBatchItemSource
{
    private readonly int _count = rootElement.GetArrayLength();
    private JsonElement.ArrayEnumerator _items = rootElement.EnumerateArray();

    public readonly int? KnownCount => _count;

    public readonly int ScanCount() => _count;

    public bool TryGetNext(out JsonRpcRequest? request, out JsonDocument? ownedDocument, out Exception? decodeException)
    {
        request = null;
        ownedDocument = null;
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
internal struct MemoryBatchItemSource(ReadOnlyMemory<byte> batchBody) : IJsonRpcBatchItemSource
{
    private readonly ReadOnlyMemory<byte> _batchBody = batchBody;
    private JsonReaderState _readerState;
    private int _offset;
    private bool _started;

    /// <remarks>Null because counting means a second pass over the body; the caller decides whether that is worth it.</remarks>
    public readonly int? KnownCount => null;

    public readonly int ScanCount() => JsonRpcArrayReader.CountItems(_batchBody);

    public bool TryGetNext(out JsonRpcRequest? request, out JsonDocument? ownedDocument, out Exception? decodeException)
    {
        request = null;
        ownedDocument = null;
        decodeException = null;

        if (!JsonRpcArrayReader.TryReadNextItem(_batchBody, ref _offset, ref _readerState, ref _started, out ReadOnlyMemory<byte> itemBody))
        {
            return false;
        }

        JsonDocument? requestDocument = null;
        try
        {
            if (JsonRpcRequestDecoder.TryReadObjectRequest(itemBody, out JsonRpcRequest? directRequest))
            {
                request = directRequest;
                return true;
            }

            // The envelope reader refused it, so fall back to a full parse - the item may still be a valid object
            // that only the general parser accepts.
            requestDocument = JsonDocument.Parse(itemBody);
            if (requestDocument.RootElement.ValueKind == JsonValueKind.Object)
            {
                request = JsonRpcRequestDecoder.CreateRequest(requestDocument.RootElement);
                ownedDocument = requestDocument;
                requestDocument = null;
                return true;
            }
        }
        catch (Exception ex) when (JsonRpcRequestDecoder.IsRequestDecodingException(ex))
        {
            decodeException = ex;
        }
        finally
        {
            // Non-null only when ownership was not handed over: a non-object root, or a throw part-way through.
            requestDocument?.Dispose();
        }

        return true;
    }
}
