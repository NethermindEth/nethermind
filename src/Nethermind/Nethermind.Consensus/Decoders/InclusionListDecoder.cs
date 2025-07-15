// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Decoders;

public class InclusionListDecoder(
    IEthereumEcdsa? ecdsa,
    ISpecProvider? specProvider,
    Logging.ILogManager? logManager)
{
    private readonly RecoverSignatures _recoverSignatures = new(ecdsa, specProvider, logManager);

    public IEnumerable<Transaction> DecodeAndRecover(byte[][] txBytes, IReleaseSpec spec)
    {
        Transaction[] transactions = TxsDecoder.DecodeTxs(txBytes, true).Transactions;
        _recoverSignatures.RecoverData(transactions, spec, false);
        return transactions;
    }

    public static byte[] Encode(Transaction transaction)
        => TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;

    public static byte[][] Encode(IEnumerable<Transaction> transactions)
        => [.. transactions.Select(Encode)];
}
