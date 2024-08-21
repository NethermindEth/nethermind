// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool;

public class BlobFinder(BlobTxDistinctSortedPool blobPool)
{
    public bool TryGetBlobAndProof(byte[] requestedBlobVersionedHash,
        [NotNullWhen(true)] out byte[]? blob,
        [NotNullWhen(true)] out byte[]? proof)
    {
        if (blobPool.GetBlobIndex.TryGetValue(requestedBlobVersionedHash, out List<Hash256>? txHashes)
            && txHashes[0] is not null
            && blobPool.TryGetValue(txHashes[0], out Transaction? blobTx)
            && blobTx.BlobVersionedHashes?.Length > 0)
        {
            for (int indexOfBlob = 0; indexOfBlob < blobTx.BlobVersionedHashes.Length; indexOfBlob++)
            {
                if (Bytes.AreEqual(blobTx.BlobVersionedHashes[indexOfBlob], requestedBlobVersionedHash)
                    && blobTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper)
                {
                    blob = wrapper.Blobs[indexOfBlob];
                    proof = wrapper.Proofs[indexOfBlob];
                    return true;
                }
            }
        }

        blob = default;
        proof = default;
        return false;
    }
}
