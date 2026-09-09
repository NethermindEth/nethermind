// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Autofac.Features.AttributeFilters;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;
using Nethermind.State.Snap;
using Nethermind.State.SnapServer;

namespace Nethermind.Synchronization.SnapSync;

public sealed class SnapServer(
    ISnapStateServer stateServer,
    [KeyFilter(DbNames.Code)] IReadOnlyKeyValueStore codeDb,
    IBlockTree blockTree,
    IBlockAccessListStore blockAccessListStore) : ISnapServer
{
    private const long HardResponseByteLimit = ISnapStateServer.HardResponseByteLimit;

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

            byte[]? code = codeDb[codeHash.Bytes];
            if (code is not null)
            {
                writer.WriteValue(code);
                currentByteCount += code.Length;
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

        foreach (ValueHash256 blockHash in blockHashes)
        {
            if (currentByteCount > byteLimit || cancellationToken.IsCancellationRequested) break;

            Hash256 hash = blockHash.ToCommitment();
            BlockHeader? header = blockTree.FindHeader(hash, BlockTreeLookupOptions.TotalDifficultyNotNeeded);
            using MemoryManager<byte>? balRlp = header?.BlockAccessListHash is null
                ? null
                : blockAccessListStore.GetRlp(header.Number, hash);

            if (balRlp is null)
            {
                writer.WriteValue([]);
                currentByteCount += 1;
                continue;
            }

            ReadOnlySpan<byte> span = balRlp.Memory.Span;
            writer.WriteValue(span);
            currentByteCount += span.Length;
        }

        writer.Dispose();
        return new RlpByteArrayList(builder.ToRlpItemList());
    }
}
