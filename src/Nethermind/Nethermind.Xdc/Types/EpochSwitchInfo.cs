// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Collections.Generic;

namespace Nethermind.Xdc.Types;

public class EpochSwitchInfo(Address[] masternodes, Address[] StandbyNodes, Address[] penalties, BlockRoundInfo epochSwitchBlockInfo)
{
    public Address[] Masternodes { get; set; } = masternodes;
    public Address[] StandbyNodes { get; } = StandbyNodes;
    public Address[] Penalties { get; set; } = penalties;
    public BlockRoundInfo EpochSwitchBlockInfo { get; set; } = epochSwitchBlockInfo;
}
