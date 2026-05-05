// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using CkzgLib;
using Nethermind.Core;
using System;
using System.Text.Json.Serialization;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Holds blobs of a block.
///
/// See <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/experimental/blob-extension.md#blobsbundlev1">BlobsBundleV1</a>
/// </summary>
public class BlobsBundleV2
{
    public BlobsBundleV2(Block block)
    {
        try
        {
            int blobsCount = 0;
            foreach (Transaction? tx in block.Transactions)
            {
                blobsCount += tx?.GetBlobCount() ?? 0;
            }

            Commitments = new byte[blobsCount][];
            Blobs = new byte[blobsCount][];
            Proofs = new byte[blobsCount * Ckzg.CellsPerExtBlob][];
            int blockIndex = 0;

            foreach (Transaction? tx in block.Transactions)
            {
                if (!tx.SupportsBlobs)
                {
                    continue;
                }

                if (tx is not { NetworkWrapper: ShardBlobNetworkWrapper { Version: ProofVersion.V1 } wrapper })
                {
                    throw new ArgumentException("Shard blob transaction should contain network wrapper data");
                }

                for (int txIndex = 0;
                     txIndex < wrapper.Blobs.Length;
                     blockIndex++, txIndex++)
                {
                    Commitments[blockIndex] = wrapper.Commitments[txIndex];
                    Blobs[blockIndex] = wrapper.Blobs[txIndex];
                    Array.Copy(wrapper.Proofs, txIndex * Ckzg.CellsPerExtBlob, Proofs, blockIndex * Ckzg.CellsPerExtBlob, Ckzg.CellsPerExtBlob);
                }
            }
        }
        catch (ArgumentException)
        {

            Commitments = [];
            Blobs = [];
            Proofs = [];
        }
    }

    [JsonConstructor]
    public BlobsBundleV2(byte[][] commitments, byte[][] blobs, byte[][] proofs)
    {
        Commitments = commitments;
        Blobs = blobs;
        Proofs = proofs;
    }

    public byte[][] Commitments { get; }
    public byte[][] Blobs { get; }
    public byte[][] Proofs { get; }
}

