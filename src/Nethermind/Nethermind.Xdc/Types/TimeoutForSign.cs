// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.HotStuff.Types;

using Round = ulong;

public class TimeoutForSign
{
    public Round Round { get; set; }
    public ulong GapNumber { get; set; }
    public Rlp Hash() => Rlp.Encode(this);
}
