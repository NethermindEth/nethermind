// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Newtonsoft.Json;

namespace Nethermind.Merge.Plugin.Data;

/// <summary>
/// A data object representing a block as being sent from the execution layer to the consensus layer.
///
/// See <a href="https://github.com/ethereum/execution-apis/blob/main/src/engine/experimental/blob-extension.md#blobsbundlev1">BlobsBundleV1</a>
/// </summary>
public class BlobsBundleV1
{
    public BlobsBundleV1()
    {
        BlockHash = Keccak.Zero;
    }

    public BlobsBundleV1(Block block)
    {
        BlockHash = block.Hash!;
        List<Memory<byte>> kzgs = new();
        List<Memory<byte>> blobs = new();
        foreach (Transaction? tx in block.Transactions)
        {
            if (tx.Type is not TxType.Blob || tx.BlobKzgs is null || tx.Blobs is null)
            {
                continue;
            }

            for (int cc = 0, bc = 0;
                 cc < tx.BlobKzgs.Length;
                 cc += Ckzg.Ckzg.BytesPerCommitment, bc += Ckzg.Ckzg.BytesPerBlob)
            {
                kzgs.Add(tx.BlobKzgs.AsMemory(cc, cc + Ckzg.Ckzg.BytesPerCommitment));
                blobs.Add(tx.Blobs.AsMemory(bc, bc + Ckzg.Ckzg.BytesPerBlob));
            }
        }

        Kzgs = kzgs.ToArray();
        Blobs = blobs.ToArray();
    }

    public Memory<byte>[] Kzgs { get; set; } = Array.Empty<Memory<byte>>();
    public Memory<byte>[] Blobs { get; set; } = Array.Empty<Memory<byte>>();

    [JsonProperty(NullValueHandling = NullValueHandling.Include)]
    public Keccak? BlockHash { get; set; } = null!;
}
