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


    public IEnumerable<Transaction> Decode(byte[][] txBytes, IReleaseSpec spec)
    {
        Transaction[] txs = txBytes
            .AsParallel()
            .Select((txBytes) => TxDecoder.Instance.Decode(txBytes, RlpBehaviors.SkipTypedWrapping))
            .ToArray();

        _recoverSignatures.RecoverData(txs, spec, false);
        return txs;
    }

    public static byte[] Encode(Transaction transaction)
        => TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;

    public static byte[][] Encode(IEnumerable<Transaction> transactions)
        => [.. transactions.Select(Encode)];
}
