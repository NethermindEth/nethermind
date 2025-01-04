// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions;

public class InclusionListTxSource : ITxSource
{
    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null)
        => payloadAttributes?.InclusionListTransactions ?? [];
}
