// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;

/// <summary>EIP-8518 <c>GetProofWrapperManifests</c>: the transaction hashes of 1 to 32 distinct wrappers the responder announced.</summary>
public sealed class GetProofWrapperManifestsMessage(IReadOnlyList<ValueHash256> hashes, bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    public override int PacketType => Eth76MessageCode.GetProofWrapperManifests;
    public override string Protocol => "eth";

    public IReadOnlyList<ValueHash256> Hashes { get; } = hashes;

    public GetProofWrapperManifestsMessage(long requestId, IReadOnlyList<ValueHash256> hashes) : this(hashes, false) => RequestId = requestId;

    public override string ToString() => $"{nameof(GetProofWrapperManifestsMessage)}({RequestId}, {Hashes.Count})";
}
