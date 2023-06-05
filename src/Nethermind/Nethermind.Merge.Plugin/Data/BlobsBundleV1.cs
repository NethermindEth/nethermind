// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

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
            blobsCount += tx?.BlobVersionedHashes?.Length ?? 0;
        }

        Commitments = new byte[blobsCount][];
        Blobs = new byte[blobsCount][];
        Proofs = new byte[blobsCount][];
        int blockIndex = 0;

        foreach (Transaction? tx in block.Transactions)
        {
            if (tx is not { NetworkWrapper: ShardBlobNetworkWrapper wrapper })
            {
                continue;
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

    public byte[][] Commitments { get; }
    public byte[][] Blobs { get; }
    public byte[][] Proofs { get; }
}
