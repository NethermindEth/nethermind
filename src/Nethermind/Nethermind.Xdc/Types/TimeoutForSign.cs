// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Xdc.Types;

public class TimeoutForSign(ulong round, ulong gapNumber)
{
    public ulong Round { get; set; } = round;
    public ulong GapNumber { get; set; } = gapNumber;
    public Hash256 SigHash() => Keccak.Compute(Rlp.Encode(this).Bytes);
}
