// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.TxPool.Collections;

namespace Nethermind.TxPool;

public class BlobCatcher(BlobTxDistinctSortedPool blobPool)
{
    public IEnumerable<BlobAndProofV1?> GetBlobsAndProofs(byte[][] request)
    {
        foreach (byte[] requestedBlobVersionedHash in request)
        {
            yield return GetBlobAndProofV1(requestedBlobVersionedHash);
        }
    }

    private BlobAndProofV1? GetBlobAndProofV1(byte[] requestedBlobVersionedHash)
    {
        if (blobPool.GetBlobIndex.TryGetValue(requestedBlobVersionedHash, out List<Hash256>? txHashes)
            && txHashes[0] is not null
            && blobPool.TryGetValue(txHashes[0], out Transaction? blobTx)
            && blobTx.BlobVersionedHashes?.Length > 0)
        {
            for (int indexOfBlob = 0; indexOfBlob < blobTx.BlobVersionedHashes.Length; indexOfBlob++)
            {
                if (blobTx.BlobVersionedHashes[indexOfBlob] == requestedBlobVersionedHash
                    && blobTx.NetworkWrapper is ShardBlobNetworkWrapper wrapper)
                {
                    return new BlobAndProofV1(wrapper.Blobs[indexOfBlob], wrapper.Proofs[indexOfBlob]);
                }
            }
        }

        return null;
    }
}
