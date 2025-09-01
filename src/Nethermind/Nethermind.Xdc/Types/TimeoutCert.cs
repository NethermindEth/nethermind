// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;

namespace Nethermind.Consensus.HotStuff.Types;

using Round = ulong;

public class TimeoutCert
{
    public Round Round { get; set; }
    public List<byte[]> Signatures { get; set; }
    public ulong GapNumber { get; set; }
}
