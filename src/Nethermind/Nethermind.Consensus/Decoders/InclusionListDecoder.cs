// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Decoders;

/// <summary>
/// EIP-7805 (FOCIL) IL transaction codec: skip-errors RLP decode + skip-errors sender/EIP-7702
/// recovery, both delegated to shared infrastructure. Per spec, unparsable items are dropped and
/// failed recoveries leave SenderAddress null (validator treats null-sender as not-appendable).
/// </summary>
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
        => TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;

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
