// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Extensions;
using System;
using System.Linq;
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

            Commitments = new Memory<byte>[blobsCount];
            Blobs = new Memory<byte>[blobsCount];
            Proofs = new Memory<byte>[blobsCount * Ckzg.Ckzg.CellsPerExtBlob];
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
                     txIndex < wrapper.Count;
                     blockIndex++, txIndex++)
                {
                    Commitments[blockIndex] = wrapper.CommitmentAt(txIndex);
                    Blobs[blockIndex] = wrapper.BlobAt(txIndex);
                    int i = 0;
                    foreach (var proof in wrapper.ProofsAt(txIndex).Chunk(Ckzg.Ckzg.BytesPerProof))
                    {
                        Proofs[blockIndex * Ckzg.Ckzg.CellsPerExtBlob + i++] = proof;
                    }
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
    public BlobsBundleV2(Memory<byte>[] commitments, Memory<byte>[] blobs, Memory<byte>[] proofs)
    {
        Commitments = commitments;
        Blobs = blobs;
        Proofs = proofs;
    }

    public Memory<byte>[] Commitments { get; }
    public Memory<byte>[] Blobs { get; }
    public Memory<byte>[] Proofs { get; }
}

