// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Mev.Data
{
    public partial class MevMegabundle
    {
        private static Keccak GetHash(MevMegabundle bundle)
        {
            RlpStream stream = EncodeRlp(bundle);
            return Keccak.Compute(stream.Data);
        }

        private static RlpStream EncodeRlp(MevMegabundle bundle)
        {
            (int Content, int Tx, int RevertingTx) GetContentLength()
            {
                int txHashes = Rlp.LengthOfKeccakRlp * bundle.Transactions.Count;
                int revertingTxHashes = Rlp.LengthOfKeccakRlp * bundle.RevertingTxHashes.Length;
                int content = Rlp.LengthOfSequence(txHashes) + Rlp.LengthOf(bundle.BlockNumber) +
                              Rlp.LengthOf(bundle.MinTimestamp) + Rlp.LengthOf(bundle.MaxTimestamp) +
                              Rlp.LengthOfSequence(revertingTxHashes);
                return (Rlp.LengthOfSequence(content), txHashes, revertingTxHashes);
            }

            (int contentLength, int txLength, int revertingTxLength) = GetContentLength();
            RlpStream stream = new(contentLength);
            stream.StartSequence(contentLength);

            stream.StartSequence(txLength);
            for (int i = 0; i < bundle.Transactions.Count; i++)
            {
                stream.Encode(bundle.Transactions[i].Hash);
            }

            stream.Encode(bundle.BlockNumber);
            stream.Encode(bundle.MinTimestamp);
            stream.Encode(bundle.MaxTimestamp);

            stream.StartSequence(revertingTxLength);
            for (int i = 0; i < bundle.RevertingTxHashes.Length; i++)
            {
                stream.Encode(bundle.RevertingTxHashes[i]);
            }

            return stream;
        }
    }
}
