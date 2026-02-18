// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Xdc;

public class MasternodesStatus
{
    public ulong Epoch { get; set; }
    public ulong Number { get; set; }
    public UInt256 Round { get; set; }
    public int MasternodesLen { get; set; }
    public Address[]? Masternodes { get; set; }
    public int PenaltyLen { get; set; }
    public Address[]? Penalty { get; set; }
    public int StandbynodesLen { get; set; }
    public Address[]? Standbynodes { get; set; }
}
