// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using System.Collections.Generic;

namespace Nethermind.Consensus.HotStuff.Types;

using Round = ulong;

public class TimeoutCert
{
    public TimeoutCert(ulong round, List<Signature> signatures, ulong gapNumber)
    {
        Round = round;
        Signatures = signatures;
        GapNumber = gapNumber;
    }

    public Round Round { get; set; }
    public List<Signature> Signatures { get; set; }
    public ulong GapNumber { get; set; }
}
