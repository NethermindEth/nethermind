// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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

    // EIP-7805 (FOCIL): scope the decoded IL to its build, keyed by the build's PayloadAttributes
    // array, so a concurrent FCU can't leak another build's IL. Weak keys collect with the build.
    private readonly ConditionalWeakTable<byte[][], Transaction[]> _decodedByAttributes = [];

    // gasLimit is ignored — the downstream producer-side tx selection pipeline enforces it.
    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, ulong gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        => payloadAttributes?.InclusionListTransactions is { } il && _decodedByAttributes.TryGetValue(il, out Transaction[]? txs)
            ? txs
            : [];

    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec)
        => _decodedByAttributes.AddOrUpdate(inclusionListTransactions, _decoder.Value.DecodeAndRecover(inclusionListTransactions, spec));

    public bool SupportsBlobs => false;
}
