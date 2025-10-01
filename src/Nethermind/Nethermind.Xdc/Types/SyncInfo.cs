// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Types;

public class SyncInfo(QuorumCert highestQuorumCert, TimeoutCertificate highestTimeoutCertificate)
{
    public QuorumCert HighestQuorumCert { get; set; } = highestQuorumCert;
    public TimeoutCertificate HighestTimeoutCertificate { get; set; } = highestTimeoutCertificate;

    public Hash256 SigHash() => Keccak.Compute(Rlp.Encode(this).Bytes);
}
