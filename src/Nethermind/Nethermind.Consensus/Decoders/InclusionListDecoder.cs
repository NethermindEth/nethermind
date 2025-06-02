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
    TxPool.ITxPool? txPool,
    ISpecProvider? specProvider,
    Logging.ILogManager? logManager)
{
    private readonly RecoverSignatures _recoverSignatures = new(ecdsa, txPool, specProvider, logManager);


    public static IEnumerable<Transaction> Decode(byte[][] txBytes)
        => txBytes
            .AsParallel()
            .Select((txBytes) => TxDecoder.Instance.Decode(txBytes, RlpBehaviors.SkipTypedWrapping));


    public IEnumerable<Transaction> DecodeAndRecover(byte[][] txBytes, IReleaseSpec spec)
    {
        Transaction[] transactions = [.. Decode(txBytes)];
        _recoverSignatures.RecoverData(transactions, spec, false);
        return transactions;
    }

    public static byte[] Encode(Transaction transaction)
        => TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;

    public static byte[][] Encode(IEnumerable<Transaction> transactions)
        => [.. transactions.Select(Encode)];
}
