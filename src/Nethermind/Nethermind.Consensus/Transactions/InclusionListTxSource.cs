// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Consensus.Decoders;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Consensus.Transactions;

public class InclusionListTxSource(
    IEthereumEcdsa? ecdsa,
    TxPool.ITxPool? txPool,
    ISpecProvider? specProvider,
    Logging.ILogManager? logManager) : ITxSource
{
    private IEnumerable<Transaction> _inclusionListTransactions = [];
    private readonly InclusionListDecoder _inclusionListDecoder = new(ecdsa, txPool, specProvider, logManager);

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        => _inclusionListTransactions;

    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec)
    {
        _inclusionListTransactions = _inclusionListDecoder.Decode(inclusionListTransactions, spec);
    }

    public bool SupportsBlobs => false;
}
