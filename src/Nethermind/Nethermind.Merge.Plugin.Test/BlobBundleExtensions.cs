// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Crypto;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Merge.Plugin.Test;

internal static class BlobBundleExtensions
{
    public static byte[][] GetBlobVersionedHashes(this BlobsBundleV1 blobsBundle)
    {
        byte[][] hashes = new byte[blobsBundle.Commitments.Length][];

        for (var i = 0; i < blobsBundle.Commitments.Length; i++)
        {
            hashes[i] = new byte[KzgPolynomialCommitments.BytesPerBlobVersionedHash];
            KzgPolynomialCommitments.TryComputeCommitmentHashV1(blobsBundle.Commitments[i], hashes[i]);
        }

        return hashes;
    }
}
