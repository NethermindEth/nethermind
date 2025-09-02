// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Types;

public class SyncInfo
{
    public SyncInfo(QuorumCert highestQuorumCert, TimeoutCert highestTimeoutCert)
    {
        HighestQuorumCert = highestQuorumCert;
        HighestTimeoutCert = highestTimeoutCert;
    }

    public QuorumCert HighestQuorumCert { get; set; }
    public TimeoutCert HighestTimeoutCert { get; set; }

    public Hash256 SigHash() => Keccak.Compute(Rlp.Encode(this).Bytes);
}
