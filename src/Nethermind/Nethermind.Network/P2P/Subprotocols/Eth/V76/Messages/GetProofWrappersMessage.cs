// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;

/// <summary>EIP-8518 <c>GetProofWrappers</c>: 1 to 32 distinct wrappers the responder announced, in full or, with
/// <see cref="Compact"/>, with transactions it knows the requester holds replaced by their encoding hashes.</summary>
public sealed class GetProofWrappersMessage(IReadOnlyList<ValueHash256> hashes, bool compact, bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    public override int PacketType => Eth76MessageCode.GetProofWrappers;
    public override string Protocol => "eth";

    public IReadOnlyList<ValueHash256> Hashes { get; } = hashes;
    public bool Compact { get; } = compact;

    public GetProofWrappersMessage(long requestId, IReadOnlyList<ValueHash256> hashes, bool compact) : this(hashes, compact, false) =>
        RequestId = requestId;

    public override string ToString() => $"{nameof(GetProofWrappersMessage)}({RequestId}, {Hashes.Count}, compact: {Compact})";
}
