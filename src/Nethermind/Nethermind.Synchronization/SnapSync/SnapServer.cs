// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Headers;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;

namespace Nethermind.Synchronization.SnapSync;

public sealed class SnapServer(
    ISnapStateServer stateServer,
    [KeyFilter(DbNames.Code)] IReadOnlyKeyValueStore codeDb,
    IHeaderStore headerStore,
    IBlockAccessListStore blockAccessListStore) : ISnapServer
{
    public bool CanServe => stateServer.CanServe;

    private const long HardResponseByteLimit = ISnapStateServer.HardResponseByteLimit;
    private const int CodeReadChunkSize = 64;
    private const int AccessListReadChunkSize = 16;
    private const int AccessListSizeEstimate = 128 * 1024;


    public IByteArrayList? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, CancellationToken cancellationToken) =>
        stateServer.GetTrieNodes(pathSet, rootHash, cancellationToken);

    public IByteArrayList? GetTrieNodes(IReadOnlyList<PathGroup> pathSet, Hash256 rootHash, long byteLimit, CancellationToken cancellationToken) =>
        stateServer.GetTrieNodes(pathSet, rootHash, byteLimit, cancellationToken);

    public (IOwnedReadOnlyList<PathWithAccount>, IByteArrayList) GetAccountRanges(Hash256 rootHash,
        in ValueHash256 startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken) =>
        stateServer.GetAccountRanges(rootHash, startingHash, limitHash, byteLimit, cancellationToken);

    public (IOwnedReadOnlyList<IOwnedReadOnlyList<PathWithStorageSlot>>, IByteArrayList?) GetStorageRanges(
        Hash256 rootHash,
        IReadOnlyList<PathWithAccount> accounts,
        in ValueHash256? startingHash,
        in ValueHash256? limitHash,
        long byteLimit,
        CancellationToken cancellationToken) =>
        stateServer.GetStorageRanges(rootHash, accounts, startingHash, limitHash, byteLimit, cancellationToken);

    public IByteArrayList GetByteCodes(IReadOnlyList<ValueHash256> requestedHashes, long byteLimit, CancellationToken cancellationToken)
    {
        if (byteLimit > HardResponseByteLimit)
            byteLimit = HardResponseByteLimit;

        using DeferredRlpItemList.Builder builder = new(requestedHashes.Count);
        DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer();
        CodeResponseWriter codes = new(builder, byteLimit, cancellationToken);
        Span<byte> keys = stackalloc byte[CodeReadChunkSize * ValueHash256.MemorySize];
        int pending = 0;
        // A peer sets the byte limit, so chunks start at one code and double after each full chunk: a batch
        // never reads more codes past the limit than were read before it.
        int chunkSize = 1;

        foreach (ValueHash256 codeHash in requestedHashes)
        {
            if (codeHash.Bytes.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                ReadCodes(keys[..(pending * ValueHash256.MemorySize)], codes);
                chunkSize = NextChunkSize(chunkSize, pending);
                pending = 0;
                if (codes.IsFull) break;

                codes.WriteEmpty();
                continue;
            }

            codeHash.Bytes.CopyTo(keys[(pending * ValueHash256.MemorySize)..]);
            if (++pending < chunkSize) continue;

            ReadCodes(keys[..(pending * ValueHash256.MemorySize)], codes);
            chunkSize = NextChunkSize(chunkSize, pending);
            pending = 0;
            if (codes.IsFull) break;
        }

        ReadCodes(keys[..(pending * ValueHash256.MemorySize)], codes);
        writer.Dispose();
        return new RlpByteArrayList(builder.ToRlpItemList());
    }

    private static int NextChunkSize(int chunkSize, int read) =>
        read < chunkSize ? chunkSize : Math.Min(chunkSize * 2, CodeReadChunkSize);

    /// <summary>Writes the stored codes for <paramref name="keys"/> in order, skipping missing ones.</summary>
    /// <remarks>
    /// A batch reads the whole chunk even past the byte limit, which the growing chunk size bounds.
    /// Code keys are hashes, so a cached block holds no likely next request: the reads hint a cache miss.
    /// </remarks>
    private void ReadCodes(ReadOnlySpan<byte> keys, CodeResponseWriter codes)
    {
        int count = keys.Length / ValueHash256.MemorySize;
        if (count == 0 || codes.IsFull) return;

        if (codeDb is IBatchReadOnlyDb batch)
        {
            Span<bool> found = stackalloc bool[count];
            batch.GetMany(keys, ValueHash256.MemorySize, found, codes, ReadFlags.HintCacheMiss);
            return;
        }

        for (int i = 0; i < count && !codes.IsFull; i++)
        {
            Span<byte> code = codeDb.GetSpan(keys.Slice(i * ValueHash256.MemorySize, ValueHash256.MemorySize), ReadFlags.HintCacheMiss);
            try
            {
                if (!code.IsNull()) codes.Decode(code);
            }
            finally
            {
                codeDb.DangerousReleaseMemory(code);
            }
        }
    }

    /// <summary>Appends served codes to the response until the byte limit is passed or the request is cancelled.</summary>
    private sealed class CodeResponseWriter(DeferredRlpItemList.Builder builder, long byteLimit, CancellationToken cancellationToken)
        : IValueDecoder<bool>
    {
        private long _byteCount;

        public bool IsFull => _byteCount > byteLimit || cancellationToken.IsCancellationRequested;

        /// <remarks>A root writer only appends to the root entry, so a fresh one writes into the same list.</remarks>
        public bool Decode(ReadOnlySpan<byte> code)
        {
            if (IsFull) return false;

            builder.BeginRootContainer().WriteValue(code);
            _byteCount += code.Length;
            return true;
        }

        public void WriteEmpty()
        {
            builder.BeginRootContainer().WriteValue([]);
            _byteCount += 1;
        }
    }

    public IByteArrayList GetBlockAccessLists(IReadOnlyList<ValueHash256> blockHashes, long byteLimit, CancellationToken cancellationToken)
    {
        if (byteLimit > HardResponseByteLimit)
            byteLimit = HardResponseByteLimit;

        long currentByteCount = 0;
        // The response buffer is sized from the first stored list and the batch buffer at the first read, so a request
        // for unknown hashes rents nothing for them.
        DeferredRlpItemList.Builder? builder = null;
        DeferredRlpItemList.Builder.Writer writer = default;
        int missingBeforeFirstList = 0;
        ArrayPoolList<byte> lists = new(0);
        Span<ValueHash256> hashes = stackalloc ValueHash256[AccessListReadChunkSize];
        Span<ulong?> numbers = stackalloc ulong?[AccessListReadChunkSize];
        Span<ValueHash256> knownHashes = stackalloc ValueHash256[AccessListReadChunkSize];
        Span<ulong> knownNumbers = stackalloc ulong[AccessListReadChunkSize];
        Span<Range?> ranges = stackalloc Range?[AccessListReadChunkSize];

        try
        {
            // A batch of lists is read whole even past the byte limit, which a peer sets. Batches start at one list
            // and grow with the lists read, but a batch takes only as many lists as fit, at the mean size so far, in the
            // bytes left, plus one to pass the limit. The rest of a split batch is read next, so the split never reads
            // past the end of the batch.
            int listReads = 0;
            for (int start = 0; start < blockHashes.Count; start += AccessListReadChunkSize)
            {
                if (currentByteCount > byteLimit || cancellationToken.IsCancellationRequested) break;

                int count = Math.Min(AccessListReadChunkSize, blockHashes.Count - start);
                for (int i = 0; i < count; i++)
                {
                    hashes[i] = blockHashes[start + i];
                }

                headerStore.GetBlockNumbers(hashes[..count], numbers[..count]);
                int known = 0;
                for (int i = 0; i < count; i++)
                {
                    if (numbers[i] is not { } number) continue;
                    knownHashes[known] = hashes[i];
                    knownNumbers[known++] = number;
                }

                int read = 0;
                int batchEnd = 0;
                int next = 0;
                for (int i = 0; i < count; i++)
                {
                    if (currentByteCount > byteLimit || cancellationToken.IsCancellationRequested) break;

                    if (numbers[i] is not null && next == read)
                    {
                        if (batchEnd == read) batchEnd = read + Math.Min(Math.Clamp(listReads, 1, AccessListReadChunkSize), known - read);
                        int batch = batchEnd - read;
                        if (currentByteCount > 0) batch = (int)Math.Min(batch, (byteLimit - currentByteCount) * listReads / currentByteCount + 1);
                        if (lists.Capacity == 0)
                        {
                            // A batch grows only to the lists read before it, so to at most half of the hashes.
                            lists.Dispose();
                            lists = [with(GetAccessListsCapacity(Math.Clamp(blockHashes.Count / 2, 1, AccessListReadChunkSize), AccessListSizeEstimate, byteLimit))];
                        }

                        lists.Clear();
                        blockAccessListStore.AppendRlps(knownNumbers.Slice(read, batch), knownHashes.Slice(read, batch), lists, ranges.Slice(read, batch));
                        read += batch;
                        listReads += batch;
                    }

                    if (numbers[i] is null || ranges[next++] is not { } range)
                    {
                        if (builder is null) missingBeforeFirstList++;
                        else writer.WriteValue([]);
                        currentByteCount += 1;
                        continue;
                    }

                    ReadOnlySpan<byte> span = lists.AsSpan()[range];
                    if (builder is null)
                    {
                        builder = new(blockHashes.Count, GetAccessListsCapacity(blockHashes.Count, span.Length, byteLimit));
                        writer = BeginAccessLists(builder, missingBeforeFirstList);
                    }

                    writer.WriteValue(span);
                    currentByteCount += span.Length;
                }
            }

            if (builder is null)
            {
                builder = new(blockHashes.Count);
                writer = BeginAccessLists(builder, missingBeforeFirstList);
            }

            writer.Dispose();
            return new RlpByteArrayList(builder.ToRlpItemList());
        }
        finally
        {
            builder?.Dispose();
            lists.Dispose();
        }
    }

    private static DeferredRlpItemList.Builder.Writer BeginAccessLists(DeferredRlpItemList.Builder builder, int missingLists)
    {
        DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer();
        for (int i = 0; i < missingLists; i++)
        {
            writer.WriteValue([]);
        }

        return writer;
    }

    /// <summary>The initial capacity of a buffer for <paramref name="lists"/> access lists of <paramref name="listSize"/> bytes, at most <paramref name="byteLimit"/>.</summary>
    /// <remarks>
    /// Sized up front, so the buffer does not double as it fills and copy itself at each step. A response stops after
    /// the list that passes the limit, and the pool rounds the limit up to a power of two, which usually holds that list.
    /// </remarks>
    private static int GetAccessListsCapacity(int lists, int listSize, long byteLimit) =>
        (int)Math.Min((long)lists * listSize, Math.Max(byteLimit, 0));
}
