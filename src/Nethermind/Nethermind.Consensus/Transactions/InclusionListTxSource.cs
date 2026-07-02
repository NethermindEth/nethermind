// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
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
    // Lazy<T> defaults to ExecutionAndPublication — once-only construction even under racing FCUs.
    private readonly Lazy<InclusionListDecoder> _decoder = new(() => new InclusionListDecoder(ecdsa, specProvider, logManager));
    private IEnumerable<Transaction> _inclusionListTransactions = [];

    // gasLimit is ignored — the downstream producer-side tx selection pipeline enforces it.
    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, ulong gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        => Volatile.Read(ref _inclusionListTransactions);

    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec)
        => Volatile.Write(ref _inclusionListTransactions, _decoder.Value.DecodeAndRecover(inclusionListTransactions, spec));

    public bool SupportsBlobs => false;
}
