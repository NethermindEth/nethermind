// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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

        Commitments = new Memory<byte>[blobsCount];
        Blobs = new Memory<byte>[blobsCount];
        Proofs = new Memory<byte>[blobsCount];
        int i = 0;

        foreach (Transaction? tx in block.Transactions)
        {
            if (!tx.SupportsBlobs)
            {
                continue;
            }

            if (tx.NetworkWrapper is not ShardBlobNetworkWrapper wrapper)
            {
                throw new ApplicationException("Blob transaction is not in the network form");
            }

            for (int cc = 0, bc = 0, pc = 0;
                 cc < wrapper.Commitments.Length;
                 i++,
                 cc += Ckzg.Ckzg.BytesPerCommitment,
                 bc += Ckzg.Ckzg.BytesPerBlob,
                 pc += Ckzg.Ckzg.BytesPerProof)
            {
                Commitments[i] = wrapper.Commitments.AsMemory(cc, Ckzg.Ckzg.BytesPerCommitment);
                Blobs[i] = wrapper.Blobs.AsMemory(bc, Ckzg.Ckzg.BytesPerBlob);
                Proofs[i] = wrapper.Proofs.AsMemory(pc, Ckzg.Ckzg.BytesPerProof);
            }
        }
    }

    public Memory<byte>[] Commitments { get; }
    public Memory<byte>[] Blobs { get; }
    public Memory<byte>[] Proofs { get; }
}
