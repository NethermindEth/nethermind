// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core.Collections;
using Nethermind.Network.P2P.Subprotocols.Eth.V66.Messages;

namespace Nethermind.Network.P2P.Subprotocols.Eth.V74.Messages;

/// <summary>EIP-8512 <c>DependencyProofs</c>: one proof per requested block, in request order.</summary>
/// <remarks>An empty entry is either an unavailable proof or the empty proof of a block without dependencies; only the block
/// header's <c>dependency_proof_hash</c> tells them apart. A responder past the soft limit returns fewer entries.</remarks>
public class DependencyProofsMessage(IOwnedReadOnlyList<byte[]> proofs, bool generateRandomRequestId = true)
    : Eth66MessageBase(generateRandomRequestId)
{
    private IOwnedReadOnlyList<byte[]>? _proofs = proofs;

    public override int PacketType => Eth74MessageCode.DependencyProofs;
    public override string Protocol => "eth";

    public IOwnedReadOnlyList<byte[]> Proofs => _proofs ?? throw new ObjectDisposedException(nameof(DependencyProofsMessage));

    public DependencyProofsMessage(long requestId, IOwnedReadOnlyList<byte[]> proofs)
        : this(proofs, false) => RequestId = requestId;

    /// <summary>Transfers the proofs to the caller so disposing this message does not dispose them.</summary>
    public IOwnedReadOnlyList<byte[]> DisownProofs()
    {
        IOwnedReadOnlyList<byte[]> proofs = Proofs;
        _proofs = null;
        return proofs;
    }

    public override string ToString() => $"{nameof(DependencyProofsMessage)}({RequestId}, {Proofs.Count})";

    public override void Dispose()
    {
        base.Dispose();
        _proofs?.Dispose();
        _proofs = null;
    }
}
