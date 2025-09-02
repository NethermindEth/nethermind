// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Types;

using Round = ulong;

public class TimeoutForSign
{
    public TimeoutForSign(ulong round, ulong gapNumber)
    {
        Round = round;
        GapNumber = gapNumber;
    }

    public Round Round { get; set; }
    public ulong GapNumber { get; set; }
    public Hash256 SigHash() => Keccak.Compute(Rlp.Encode(this).Bytes);
}
