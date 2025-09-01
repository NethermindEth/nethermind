// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Collections.Generic;

namespace Nethermind.Xdc.Types;

public class EpochSwitchInfo
{
    public EpochSwitchInfo(Address[] penalties, Address[] standbynodes, Address[] masternodes, BlockInfo epochSwitchBlockInfo, BlockInfo epochSwitchParentBlockInfo)
    {
        Penalties = penalties;
        Standbynodes = standbynodes;
        Masternodes = masternodes;
        EpochSwitchBlockInfo = epochSwitchBlockInfo;
        EpochSwitchParentBlockInfo = epochSwitchParentBlockInfo;
    }

    public Address[] Penalties { get; set; }
    public Address[] Standbynodes { get; set; }
    public Address[] Masternodes { get; set; }
    public BlockInfo EpochSwitchBlockInfo { get; set; }
    public BlockInfo EpochSwitchParentBlockInfo { get; set; }
}
