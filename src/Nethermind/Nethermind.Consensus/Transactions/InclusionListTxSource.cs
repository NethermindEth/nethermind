// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
/// Holds the most recent EIP-7805 inclusion list set by the CL via <c>engine_forkchoiceUpdatedV5</c>
/// and exposes it as an <see cref="ITxSource"/> for the block producer. The IL is overwritten
/// (drained) on every FCUv5 — including with an empty list — so the previous slot's IL never
/// leaks into the next production cycle.
/// </summary>
/// <remarks>
/// <see cref="Set"/> is invoked from the JSON-RPC handler thread; <see cref="GetTransactions"/>
/// is invoked from the producer thread. <see cref="Volatile.Write"/>/<see cref="Volatile.Read"/>
/// give the two threads happens-before semantics on the reference field without taking a lock.
/// Reference assignment is already atomic on .NET, but a volatile barrier is needed to publish
/// the updated value across CPU cores.
/// </remarks>
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
        => Volatile.Read(ref _inclusionListTransactions);

    public void Set(byte[][] inclusionListTransactions, IReleaseSpec spec)
        => Volatile.Write(ref _inclusionListTransactions, Decoder.DecodeAndRecover(inclusionListTransactions, spec));

    public bool SupportsBlobs => false;
}
