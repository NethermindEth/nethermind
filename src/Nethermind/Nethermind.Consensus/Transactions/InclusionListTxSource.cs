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
    // Lazy: test stubs construct with null deps (decoder/recoverer null-check on ctor). The lazy
    // race is harmless — the decoder is stateless, duplicate instances behave identically.
    private InclusionListDecoder? _decoder;
    private InclusionListDecoder Decoder => _decoder ??= new InclusionListDecoder(ecdsa, specProvider, logManager);
    private IEnumerable<Transaction> _inclusionListTransactions = [];

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        => Volatile.Read(ref _inclusionListTransactions);

    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec)
        => Volatile.Write(ref _inclusionListTransactions, Decoder.DecodeAndRecover(inclusionListTransactions, spec));

    public bool SupportsBlobs => false;
}
