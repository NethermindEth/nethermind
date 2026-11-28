// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using Nethermind.Core.Crypto;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V76.Messages;

/// <summary>One served manifest: a wrapper's hash and its ascending consensus transaction hashes.</summary>
public readonly record struct ServedManifest(ValueHash256 Hash, IReadOnlyList<ValueHash256> TransactionHashes);

/// <summary>EIP-8518 <c>ProofWrapperManifests</c>: the manifests of the available requested wrappers, in request order.</summary>
public sealed class ProofWrapperManifestsMessage(IReadOnlyList<ServedManifest> manifests, bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    public override int PacketType => Eth76MessageCode.ProofWrapperManifests;
    public override string Protocol => "eth";

    public IReadOnlyList<ServedManifest> Manifests { get; } = manifests;

    public ProofWrapperManifestsMessage(long requestId, IReadOnlyList<ServedManifest> manifests) : this(manifests, false) => RequestId = requestId;

    public override string ToString() => $"{nameof(ProofWrapperManifestsMessage)}({RequestId}, {Manifests.Count})";
}
