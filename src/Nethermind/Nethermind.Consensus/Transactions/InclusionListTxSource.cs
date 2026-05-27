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
    ISpecProvider? specProvider,
    Logging.ILogManager? logManager) : ITxSource
{
    private IEnumerable<Transaction> _inclusionListTransactions = [];

    // Lazy: decoder needs a non-null ecdsa, but unit-test fixtures that don't exercise IL
    // injection construct this with all-nulls. Building it on first Set() lets those tests
    // keep working without leaking dependencies they don't actually need.
    private InclusionListDecoder? _decoder;
    private InclusionListDecoder Decoder => _decoder ??= new InclusionListDecoder(ecdsa, specProvider, logManager);

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        => _inclusionListTransactions;

    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec)
        => _inclusionListTransactions = Decoder.DecodeAndRecover(inclusionListTransactions, spec);

    public bool SupportsBlobs => false;
}
