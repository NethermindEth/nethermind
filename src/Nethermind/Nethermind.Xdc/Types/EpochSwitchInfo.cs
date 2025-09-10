// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Collections.Generic;

namespace Nethermind.Xdc.Types;

public class EpochSwitchInfo(Address[] penalties, Address[] standbynodes, Address[] masternodes, BlockInfo epochSwitchBlockInfo, BlockInfo epochSwitchParentBlockInfo)
{
    public Address[] Penalties { get; set; } = penalties;
    public Address[] Standbynodes { get; set; } = standbynodes;
    public Address[] Masternodes { get; set; } = masternodes;
    public BlockInfo EpochSwitchBlockInfo { get; set; } = epochSwitchBlockInfo;
    public BlockInfo EpochSwitchParentBlockInfo { get; set; } = epochSwitchParentBlockInfo;
}
