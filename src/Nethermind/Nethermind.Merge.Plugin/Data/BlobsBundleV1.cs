// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// Holds blobs of a block.
///
/// See <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/experimental/blob-extension.md#blobsbundlev1">BlobsBundleV1</a>
/// </summary>
public class BlobsBundleV1
{
    public BlobsBundleV1()
    {
    }

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
            if (tx.Type is not TxType.Blob || tx.BlobKzgs is null || tx.Blobs is null)
            {
                continue;
            }

            for (int cc = 0, bc = 0, pc = 0;
                 cc < tx.BlobKzgs.Length;
                 i++,
                 cc += Ckzg.Ckzg.BytesPerCommitment,
                 bc += Ckzg.Ckzg.BytesPerBlob,
                 pc += Ckzg.Ckzg.BytesPerProof)
            {
                Commitments[i] = tx.BlobKzgs.AsMemory(cc, Ckzg.Ckzg.BytesPerCommitment);
                Blobs[i] = tx.Blobs.AsMemory(bc, Ckzg.Ckzg.BytesPerBlob);
                Proofs[i] = tx.BlobProofs.AsMemory(pc, Ckzg.Ckzg.BytesPerProof);
            }
        }
    }

    public Memory<byte>[]? Commitments { get; set; }
    public Memory<byte>[]? Blobs { get; set; }
    public Memory<byte>[]? Proofs { get; set; }
}
