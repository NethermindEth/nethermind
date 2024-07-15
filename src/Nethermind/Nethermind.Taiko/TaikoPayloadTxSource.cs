// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko;

public class TaikoPayloadTxSource(ISpecProvider specProvider, ILogManager logManager) : ITxSource
{
    private readonly EthereumEcdsa Ecdsa = new(specProvider.ChainId, logManager);

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes)
    {
        if (payloadAttributes is TaikoPayloadAttributes taikoPayloadAttributes)
        {
            RlpStream rlpStream = new(taikoPayloadAttributes.BlockMetadata!.TxList!);

            int transactionsSequenceLength = rlpStream.ReadSequenceLength();
            int transactionsCheck = rlpStream.Position + transactionsSequenceLength;

            List<Transaction> transactions = [];
            while (rlpStream.Position < transactionsCheck)
            {
                Transaction tx = Rlp.Decode<Transaction>(rlpStream, RlpBehaviors.SkipTypedWrapping);
                tx.SenderAddress ??= Ecdsa.RecoverAddress(tx);
                transactions.Add(tx);
            }

            rlpStream.Check(transactionsCheck);
        }

        return [];
    }
}
