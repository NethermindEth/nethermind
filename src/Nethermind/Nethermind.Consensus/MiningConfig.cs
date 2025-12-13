// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Config;
using Nethermind.Int256;

namespace Nethermind.Consensus;

public class MiningConfig : IMiningConfig
{
    public bool Enabled { get; set; }
    public string? Signer { get; set; }
}
