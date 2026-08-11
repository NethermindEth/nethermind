// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json.Serialization;
using Nethermind.Core;
using Nethermind.Serialization.Json;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Holds blobs of a block.
///
/// See <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/experimental/blob-extension.md#blobsbundlev1">BlobsBundleV1</a>
/// </summary>
public class BlobsBundleV1
{
    public BlobsBundleV1(Block block)
    {
        int blobsCount = 0;
        foreach (Transaction? tx in block.Transactions)
        {
            blobsCount += tx?.GetBlobCount() ?? 0;
        }

        Commitments = new byte[blobsCount][];
        Blobs = new byte[blobsCount][];
        Proofs = new byte[blobsCount][];
        int blockIndex = 0;

        foreach (Transaction? tx in block.Transactions)
        {
            // EIP-8141: include blob-carrying frame txs (type 6) as well as type-3 txs, so the bundle
            // carries every blob the header's blob gas accounts for.
            if (!tx.CarriesBlobs)
            {
                continue;
            }

            if (tx is not { NetworkWrapper: ShardBlobNetworkWrapper wrapper })
            {
                throw new ArgumentException("Shard blob transaction should contain network wrapper data");
            }

            for (int txIndex = 0;
                 txIndex < wrapper.Blobs.Length;
                 blockIndex++, txIndex++)
            {
                Commitments[blockIndex] = wrapper.Commitments[txIndex];
                Blobs[blockIndex] = wrapper.Blobs[txIndex];
                Proofs[blockIndex] = wrapper.Proofs[txIndex];
            }
        }
    }

    [JsonConstructor]
    public BlobsBundleV1(byte[][] commitments, byte[][] blobs, byte[][] proofs)
    {
        Commitments = commitments;
        Blobs = blobs;
        Proofs = proofs;
    }

    [JsonConverter(typeof(BlobsBundleByteArrayArrayConverter))] public byte[][] Commitments { get; }
    [JsonConverter(typeof(BlobsBundleByteArrayArrayConverter))] public byte[][] Blobs { get; }
    [JsonConverter(typeof(BlobsBundleByteArrayArrayConverter))] public byte[][] Proofs { get; }
}
