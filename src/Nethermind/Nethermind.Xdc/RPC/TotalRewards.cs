// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System.Collections.Generic;

namespace Nethermind.Xdc;

public class TotalRewards
{
    public Address? Address { get; set; }
    public ulong StartBlockNum { get; set; }
    public ulong EndBlockNum { get; set; }
    public UInt256? TotalAccountReward { get; set; }
    public Dictionary<string, UInt256>? TotalDelegatedReward { get; set; }
}
