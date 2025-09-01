// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.HotStuff.Types;

public class SyncInfo
{
    public QuorumCert HighestQuorumCert { get; set; }
    public TimeoutCert HighestTimeoutCert { get; set; }

    public Rlp Hash() => Rlp.Encode(this);
}
