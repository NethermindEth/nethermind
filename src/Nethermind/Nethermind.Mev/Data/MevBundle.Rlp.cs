// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Mev.Data
{
    public partial class MevBundle
    {
        private static Keccak GetHash(MevBundle bundle)
        {
            RlpStream stream = EncodeRlp(bundle);
            return Keccak.Compute(stream.Data);
        }

        private static RlpStream EncodeRlp(MevBundle bundle)
        {
            (int Content, int Tx) GetContentLength()
            {
                int txHashes = Rlp.LengthOfKeccakRlp * bundle.Transactions.Count;
                int content = Rlp.LengthOf(bundle.BlockNumber) + Rlp.LengthOfSequence(txHashes);
                return (Rlp.LengthOfSequence(content), txHashes);
            }

            (int contentLength, int txLength) = GetContentLength();
            RlpStream stream = new(contentLength);
            stream.StartSequence(contentLength);
            stream.Encode(bundle.BlockNumber);
            stream.StartSequence(txLength);

            for (int i = 0; i < bundle.Transactions.Count; i++)
            {
                stream.Encode(bundle.Transactions[i].Hash);
            }

            return stream;
        }
    }
}
