// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.State.SnapServer;

public class SnapBalServer(IBlockAccessListStore)
{
    public IByteArrayList GetBlockAccessLists(IReadOnlyList<ValueHash256> blockHashes, long byteLimit, CancellationToken cancellationToken)
    {
        using DeferredRlpItemList.Builder builder = new(blockHashes.Count);
        DeferredRlpItemList.Builder.Writer writer = builder.BeginRootContainer();

           using GetBlockAccessListsMessage req = request;
        IOwnedReadOnlyList<Hash256> hashes = req.Hashes;
        ReadOnlySpan<Hash256> hashesSpan = hashes.AsSpan();
        long totalSize = 0;
        ArrayPoolList<byte[]?> results = new(hashesSpan.Length);

        try
        {
            for (int i = 0; i < hashesSpan.Length; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                using MemoryManager<byte>? balRlp = SyncServer.GetBlockAccessListRlp(hashesSpan[i]);
                byte[]? balRlpBytes = balRlp is null ? null : balRlp.Memory.Span.ToArray();
                results.Add(balRlpBytes);
                totalSize += BlockAccessListsMessageSerializer.GetBlockAccessListEntryLength(balRlpBytes);

                if (totalSize > BalResponseSoftLimit)
                {
                    break;
                }
            }

            return Task.FromResult(new BlockAccessListsMessage(req.RequestId, results));
        }
        catch
        {
            results.Dispose();
            throw;
        }

        writer.Dispose();
        return new RlpByteArrayList(builder.ToRlpItemList());
    }
}