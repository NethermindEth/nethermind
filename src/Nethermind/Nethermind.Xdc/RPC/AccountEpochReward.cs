// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System.Collections.Generic;

namespace Nethermind.Xdc;

public class AccountEpochReward
{
    public ulong EpochBlockNum { get; set; }
    public Address? Address { get; set; }
    public string? AccountStatus { get; set; }
    public UInt256? AccountReward { get; set; }
    public Dictionary<string, UInt256>? DelegatedReward { get; set; }
}
