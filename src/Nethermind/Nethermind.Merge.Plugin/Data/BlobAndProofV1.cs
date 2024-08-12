// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.Data;

public class BlobAndProofV1
{
    public BlobAndProofV1(Transaction blobTx, int index)
    {
        if (blobTx is not { NetworkWrapper: ShardBlobNetworkWrapper wrapper })
        {
            throw new ArgumentException("Shard blob transaction should contain network wrapper data");
        }

        Blob = wrapper.Blobs[index];
        Proof = wrapper.Proofs[index];
    }
    public byte[] Blob { get; set; }
    public byte[] Proof { get; set; }
}
