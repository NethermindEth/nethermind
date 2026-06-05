// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading;
using Nethermind.Consensus.Decoders;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Crypto;

namespace Nethermind.Consensus.Transactions;

public class InclusionListTxSource(
    IEthereumEcdsa? ecdsa,
    ISpecProvider? specProvider,
    Logging.ILogManager? logManager) : ITxSource
{
    private readonly InclusionListDecoder _decoder = new(ecdsa, specProvider, logManager);
    private IEnumerable<Transaction> _inclusionListTransactions = [];

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        => Volatile.Read(ref _inclusionListTransactions);

    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec)
        => Volatile.Write(ref _inclusionListTransactions, _decoder.DecodeAndRecover(inclusionListTransactions, spec));

    public bool SupportsBlobs => false;
}
