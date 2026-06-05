// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Types;

public class SyncInfo(QuorumCertificate highestQuorumCert, TimeoutCertificate highestTimeoutCert)
{
    private static readonly SyncInfoDecoder _decoder = new();
    public QuorumCertificate HighestQuorumCert { get; set; } = highestQuorumCert;
    public TimeoutCertificate HighestTimeoutCert { get; set; } = highestTimeoutCert;

    public (ulong Round, Hash256 Hash) GetSyncInfoKey()
    {
        Hash256 hash = Keccak.Compute(_decoder.Encode(this, RlpBehaviors.ForSealing).Bytes);

        if (HighestQuorumCert is not null)
        {
            return (HighestQuorumCert.ProposedBlockInfo.Round, hash);

        }
        return (HighestTimeoutCert.Round, hash);
    }
}
