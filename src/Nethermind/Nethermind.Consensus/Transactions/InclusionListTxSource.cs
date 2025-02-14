// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.Transactions;

public class InclusionListTxSource(ulong chainId) : ITxSource
{
    private readonly EthereumEcdsa _ecdsa = new(chainId);

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
        => payloadAttributes?.InclusionListTransactions?.Select(tx => DecodeTransaction(tx)) ?? [];

    private Transaction DecodeTransaction(ReadOnlySpan<byte> txBytes)
    {
        Transaction tx = TxDecoder.Instance.Decode(txBytes, RlpBehaviors.SkipTypedWrapping);
        tx.SenderAddress = _ecdsa.RecoverAddress(tx, true);
        return tx;
    }
}
