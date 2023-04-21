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
        List<Memory<byte>> commitments = new();
        List<Memory<byte>> blobs = new();
        List<Memory<byte>> proofs = new();
        foreach (Transaction? tx in block.Transactions)
        {
            if (tx.Type is not TxType.Blob || tx.BlobKzgs is null || tx.Blobs is null)
            {
                continue;
            }

            for (int cc = 0, bc = 0, pc = 0;
                 cc < tx.BlobKzgs.Length;
                 cc += Ckzg.Ckzg.BytesPerCommitment,
                 bc += Ckzg.Ckzg.BytesPerBlob,
                 pc += Ckzg.Ckzg.BytesPerCommitment)
            {
                commitments.Add(tx.BlobKzgs.AsMemory(cc, cc + Ckzg.Ckzg.BytesPerCommitment));
                blobs.Add(tx.Blobs.AsMemory(bc, bc + Ckzg.Ckzg.BytesPerBlob));
                proofs.Add(tx.BlobProofs.AsMemory(pc, pc + Ckzg.Ckzg.BytesPerProof));
            }
        }

        Commitments = commitments.ToArray();
        Blobs = blobs.ToArray();
        Proofs = proofs.ToArray();
    }

    public Memory<byte>[]? Commitments { get; set; }
    public Memory<byte>[]? Blobs { get; set; }
    public Memory<byte>[]? Proofs { get; set; }
}
