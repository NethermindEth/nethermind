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
    private const long HardResponseByteLimit = ISnapStateServer.HardResponseByteLimit;
    private const int AccessListReadChunkSize = 16;

    public bool CanServe => stateServer.CanServe;

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

        long currentByteCount = 0;
        using DeferredRlpItemList.Builder builder = new(requestedHashes.Count);
        DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer();

        foreach (ValueHash256 codeHash in requestedHashes)
        {
            if (currentByteCount > byteLimit || cancellationToken.IsCancellationRequested) break;

            if (codeHash.Bytes.SequenceEqual(Keccak.OfAnEmptyString.Bytes))
            {
                writer.WriteValue([]);
                currentByteCount += 1;
                continue;
            }

            // Code keys are hashes, so a cached block holds no likely next request.
            Span<byte> code = codeDb.GetSpan(codeHash.Bytes, ReadFlags.HintCacheMiss);
            try
            {
                if (!code.IsNull())
                {
                    writer.WriteValue(code);
                    currentByteCount += code.Length;
                }
            }
            finally
            {
                codeDb.DangerousReleaseMemory(code);
            }
        }

        writer.Dispose();
        return new RlpByteArrayList(builder.ToRlpItemList());
    }

    public IByteArrayList GetBlockAccessLists(IReadOnlyList<ValueHash256> blockHashes, long byteLimit, CancellationToken cancellationToken)
    {
        if (byteLimit > HardResponseByteLimit)
            byteLimit = HardResponseByteLimit;

        long currentByteCount = 0;
        using DeferredRlpItemList.Builder builder = new(blockHashes.Count);
        DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer();
        using ArrayPoolList<byte> lists = new(0);
        Span<ValueHash256> hashes = stackalloc ValueHash256[AccessListReadChunkSize];
        Span<ulong?> numbers = stackalloc ulong?[AccessListReadChunkSize];
        Span<ValueHash256> knownHashes = stackalloc ValueHash256[AccessListReadChunkSize];
        Span<ulong> knownNumbers = stackalloc ulong[AccessListReadChunkSize];
        Span<Range?> ranges = stackalloc Range?[AccessListReadChunkSize];

        // A batch of lists is read whole even past the byte limit, which a peer sets. Batches start at one list
        // and grow with the lists read, so a batch never reads more lists past the limit than were read before it.
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
            int next = 0;
            for (int i = 0; i < count; i++)
            {
                if (currentByteCount > byteLimit || cancellationToken.IsCancellationRequested) break;

                if (numbers[i] is not null && next == read)
                {
                    int batch = Math.Min(Math.Clamp(listReads, 1, AccessListReadChunkSize), known - read);
                    lists.Clear();
                    blockAccessListStore.AppendRlps(knownNumbers.Slice(read, batch), knownHashes.Slice(read, batch), lists, ranges.Slice(read, batch));
                    read += batch;
                    listReads += batch;
                }

                if (numbers[i] is null || ranges[next++] is not { } range)
                {
                    writer.WriteValue([]);
                    currentByteCount += 1;
                    continue;
                }

                ReadOnlySpan<byte> span = lists.AsSpan()[range];
                writer.WriteValue(span);
                currentByteCount += span.Length;
            }
        }

        writer.Dispose();
        return new RlpByteArrayList(builder.ToRlpItemList());
    }
}
