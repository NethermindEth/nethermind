// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Types;

/// <summary>Gloas req/resp <c>ExecutionPayloadEnvelopesByRangeRequest</c> (p2p-interface.md).</summary>
[SszContainer]
public partial class ExecutionPayloadEnvelopesByRangeRequest
{
    public ulong StartSlot { get; set; }

    public ulong Count { get; set; }
}

/// <summary>
/// Gloas req/resp <c>ExecutionPayloadEnvelopeRoots</c>: bare SSZ list of beacon block roots,
/// <c>LIMIT = MAX_REQUEST_PAYLOADS</c> (p2p-interface.md). Shaped like <see cref="BeaconBlocksByRootRequest"/>.
/// </summary>
[SszContainer(isCollectionItself: true)]
public partial class ExecutionPayloadEnvelopeRoots
{
    [SszList(128)] // MAX_REQUEST_PAYLOADS
    public Hash256[]? Roots { get; set; }
}
