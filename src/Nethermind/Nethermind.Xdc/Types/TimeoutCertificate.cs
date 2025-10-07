// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Xdc.RLP;

namespace Nethermind.Xdc.Types;

public class TimeoutCertificate(ulong round, Signature[] signatures, ulong gapNumber)
{
    public ulong Round { get; set; } = round;
    public Signature[] Signatures { get; set; } = signatures;
    public ulong GapNumber { get; set; } = gapNumber;
}
