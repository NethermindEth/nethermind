// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Decoders;
using Nethermind.Consensus.Producers;
using Nethermind.Core;

namespace Nethermind.Consensus.Transactions;

public class InclusionListTxSource(ulong chainId) : ITxSource
{
    private IEnumerable<Transaction> _inclusionListTransactions = [];

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        => _inclusionListTransactions;

    public void Set(byte[][] inclusionListTransactions)
    {
        _inclusionListTransactions = InclusionListDecoder.Decode(inclusionListTransactions, chainId);
    }

    public bool SupportsBlobs => false;
}
