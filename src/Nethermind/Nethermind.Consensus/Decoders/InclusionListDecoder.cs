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
        RlpWriter writer = new(buffer);
        decoder.Encode(ref writer, transaction, RlpBehaviors.SkipTypedWrapping);
        return buffer;
    }

    public static ArrayPoolList<byte> EncodePooled(Transaction transaction)
    {
        TxDecoder decoder = TxDecoder.Instance;
        int length = decoder.GetLength(transaction, RlpBehaviors.SkipTypedWrapping);
        ArrayPoolList<byte> result = new(length, length);
        RlpWriter writer = new(new CappedArray<byte>(result.UnsafeGetInternalArray(), length));
        decoder.Encode(ref writer, transaction, RlpBehaviors.SkipTypedWrapping);
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
