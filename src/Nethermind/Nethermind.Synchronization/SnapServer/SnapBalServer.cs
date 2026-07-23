// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.State.SnapServer;

namespace Nethermind.Synchronization.SnapServer;

/// <summary>Serves block access lists (EIP-8189) by resolving each requested block hash to its number via the block
/// tree and reading the stored RLP.</summary>
public sealed class SnapBalServer(IBlockTree blockTree, IBlockAccessListStore blockAccessListStore) : ISnapBalServer
{
    public IByteArrayList GetBlockAccessLists(IReadOnlyList<ValueHash256> blockHashes, long byteLimit, CancellationToken cancellationToken)
    {
        if (byteLimit > ISnapServer.HardResponseByteLimit) byteLimit = ISnapServer.HardResponseByteLimit;

        long currentByteCount = 0;
        using DeferredRlpItemList.Builder builder = new(blockHashes.Count);
        DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer();

        foreach (ValueHash256 blockHash in blockHashes)
        {
            if (currentByteCount > byteLimit || cancellationToken.IsCancellationRequested) break;

            Hash256 hash = blockHash.ToCommitment();
            BlockHeader? header = blockTree.FindHeader(hash);
            // Missing block: skip (as SnapCodeServer does for missing code).
            // TODO(EIP-8189): confirm whether requesters require an empty positional entry (WriteValue([])) instead.
            if (header is null) continue;

            using MemoryManager<byte>? balRlp = blockAccessListStore.GetRlp(header.Number, hash);
            if (balRlp is null) continue;

            ReadOnlySpan<byte> span = balRlp.Memory.Span;
            writer.WriteValue(span);
            currentByteCount += span.Length;
        }

        writer.Dispose();
        return new RlpByteArrayList(builder.ToRlpItemList());
    }
}
