// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Decoders;

public static class InclusionListDecoder
{
    public static IEnumerable<Transaction> Decode(byte[][] transactions, ulong chainId)
        => Decode(transactions, new EthereumEcdsa(chainId));

    public static IEnumerable<Transaction> Decode(byte[][] transactions, IEthereumEcdsa ecdsa)
        => transactions.AsParallel()
            .Select((txBytes) =>
            {
                Transaction tx = TxDecoder.Instance.Decode(txBytes, RlpBehaviors.SkipTypedWrapping);
                tx.SenderAddress = ecdsa.RecoverAddress(tx, true);
                return tx;
            });

    public static byte[] Encode(Transaction transaction)
        => TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;

    public static byte[][] Encode(IEnumerable<Transaction> transactions)
        => [.. transactions.Select(Encode)];
}
