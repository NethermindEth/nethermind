// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Types;

public class SyncInfo(QuorumCertificate highestQuorumCert, TimeoutCertificate highestTimeoutCert)
{
    public QuorumCertificate HighestQuorumCert { get; set; } = highestQuorumCert;
    public TimeoutCertificate HighestTimeoutCert { get; set; } = highestTimeoutCert;

    public Hash256 SigHash() => Keccak.Compute(Rlp.Encode(this).Bytes);
}
