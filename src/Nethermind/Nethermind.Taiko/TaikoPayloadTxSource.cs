// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Taiko;

public class TaikoPayloadTxSource : ITxSource
{
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
                transactions.Add(Rlp.Decode<Transaction>(rlpStream, RlpBehaviors.SkipTypedWrapping));
            }

            rlpStream.Check(transactionsCheck);
        }

        return [];
    }
}
