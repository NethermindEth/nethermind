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

/// <summary>
/// Holds the most recent EIP-7805 inclusion list (set by the CL via FCUv5) and exposes it
/// to the block producer as an <see cref="ITxSource"/>. Overwritten on every FCUv5 — even
/// with an empty list — so the previous slot's IL never leaks into the next cycle.
/// </summary>
/// <remarks>
/// <see cref="Set"/> runs on the JSON-RPC thread, <see cref="GetTransactions"/> on the
/// producer thread; the Volatile pair publishes updates across cores without a lock.
/// </remarks>
public class InclusionListTxSource(
    IEthereumEcdsa? ecdsa,
    ISpecProvider? specProvider,
    Logging.ILogManager? logManager) : ITxSource
{
    private IEnumerable<Transaction> _inclusionListTransactions = [];

    // Lazy: unit-test fixtures construct with null ecdsa/specProvider; built on first Set().
    private InclusionListDecoder? _decoder;
    private InclusionListDecoder Decoder => _decoder ??= new InclusionListDecoder(ecdsa, specProvider, logManager);

    public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit, PayloadAttributes? payloadAttributes = null, bool filterSource = false)
        => Volatile.Read(ref _inclusionListTransactions);

    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec)
        => Volatile.Write(ref _inclusionListTransactions, Decoder.DecodeAndRecover(inclusionListTransactions, spec));

    public bool SupportsBlobs => false;
}
