// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Types;

public class SyncInfo(QuorumCertificate highestQuorumCert, TimeoutCertificate highestTimeoutCert, bool isMine = false) : RlpHashEqualityBase
{
    private static readonly SyncInfoDecoder _decoder = new();

    public QuorumCertificate HighestQuorumCert { get; set; } = highestQuorumCert;
    public TimeoutCertificate HighestTimeoutCert { get; set; } = highestTimeoutCert;

    /// <summary>Whether this is our own announcement rather than one relayed from a peer.</summary>
    public bool IsMine { get; } = isMine;

    protected override void Encode(ref KeccakRlpWriter writer) =>
        _decoder.Encode(ref writer, this, RlpBehaviors.None);
}
