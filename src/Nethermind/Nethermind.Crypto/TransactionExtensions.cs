// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Crypto
{
    public static class TransactionExtensions
    {
        private static readonly TxDecoder _txDecoder = TxDecoder.Instance;

        public static Hash256 CalculateHash(this Transaction transaction)
        {
            KeccakRlpStream stream = new();
            _txDecoder.Encode(stream, transaction, RlpBehaviors.SkipTypedWrapping);
            return stream.GetHash();
        }

        // TODO: to be removed after Fusaka
        private static void CalculateAdditionalProofs(this Transaction transaction)
        {
            ShardBlobNetworkWrapper networkWrapper = (ShardBlobNetworkWrapper)transaction.NetworkWrapper!;

            if (networkWrapper.Proofs.Length == networkWrapper.Blobs.Length)
            {
                networkWrapper.BlobProofs = networkWrapper.Proofs;
                // networkWrapper.Proofs = calculate cell proofs
            }
            else
            {
                // networkWrapper.BlobProofs = calculate blob proofs
            }
        }
    }
}
