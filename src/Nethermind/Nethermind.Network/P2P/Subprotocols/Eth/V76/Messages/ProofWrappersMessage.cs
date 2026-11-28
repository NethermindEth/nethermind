// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;

/// <summary>One served proof wrapper: its canonical hash and its RLP item, in full or compact form.</summary>
public readonly record struct ServedWrapper(ValueHash256 Hash, byte[] Item);

/// <summary>EIP-8518 <c>ProofWrappers</c>: the available requested wrappers, in request order, as embedded RLP items.</summary>
public sealed class ProofWrappersMessage(IReadOnlyList<ServedWrapper> wrappers, bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    public override int PacketType => Eth76MessageCode.ProofWrappers;
    public override string Protocol => "eth";

    public IReadOnlyList<ServedWrapper> Wrappers { get; } = wrappers;

    public ProofWrappersMessage(long requestId, IReadOnlyList<ServedWrapper> wrappers) : this(wrappers, false) => RequestId = requestId;

    public override string ToString() => $"{nameof(ProofWrappersMessage)}({RequestId}, {Wrappers.Count})";
}
