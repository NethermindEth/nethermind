// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Buffers;
using Nethermind.Core.Collections;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Decoders;

public class InclusionListDecoder(
    IEthereumEcdsa? ecdsa,
    ISpecProvider? specProvider,
    ILogManager? logManager)
{
    private readonly RecoverSignatures _recoverSignatures = new(ecdsa, specProvider, logManager);

    public Transaction[] DecodeAndRecover(byte[][] txBytes, IReleaseSpec spec)
    {
        Transaction[] txs = TxsDecoder.DecodeTxs(txBytes, skipErrors: true).Transactions;
        _recoverSignatures.RecoverData(txs, spec, skipErrors: true);
        return txs;
    }

    public static byte[] Encode(Transaction transaction)
    {
        TxDecoder decoder = TxDecoder.Instance;
        byte[] buffer = new byte[decoder.GetLength(transaction, RlpBehaviors.SkipTypedWrapping)];
        RlpStream stream = new(buffer);
        decoder.Encode(stream, transaction, RlpBehaviors.SkipTypedWrapping);
        return buffer;
    }

    /// <summary>
    /// Pool-rented variant of <see cref="Encode(Transaction)"/>. The returned list's
    /// <see cref="ArrayPoolList{T}.Count"/> equals the exact RLP length (so JSON hex serialisation
    /// writes only that many bytes, not the larger rented buffer). Caller owns disposal.
    /// </summary>
    public static ArrayPoolList<byte> EncodePooled(Transaction transaction)
    {
        TxDecoder decoder = TxDecoder.Instance;
        int length = decoder.GetLength(transaction, RlpBehaviors.SkipTypedWrapping);
        ArrayPoolList<byte> result = new(length, length);
        RlpStream stream = new(new CappedArray<byte>(result.UnsafeGetInternalArray(), length));
        decoder.Encode(stream, transaction, RlpBehaviors.SkipTypedWrapping);
        return result;
    }

    public static byte[][] Encode(Transaction[] transactions)
    {
        byte[][] result = new byte[transactions.Length][];
        for (int i = 0; i < transactions.Length; i++)
        {
            result[i] = Encode(transactions[i]);
        }
        return result;
    }
}
