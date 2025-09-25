// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Collections.Generic;

namespace Nethermind.Xdc.Types;

public class EpochSwitchInfo(Address[] penalties, Address[] standbynodes, Address[] masternodes, BlockRoundInfo epochSwitchBlockInfo, BlockRoundInfo epochSwitchParentBlockInfo)
{
    public Address[] Penalties { get; set; } = penalties;
    public Address[] Standbynodes { get; set; } = standbynodes;
    public Address[] Masternodes { get; set; } = masternodes;
    public BlockRoundInfo EpochSwitchBlockInfo { get; set; } = epochSwitchBlockInfo;
    public BlockRoundInfo EpochSwitchParentBlockInfo { get; set; } = epochSwitchParentBlockInfo;
}
