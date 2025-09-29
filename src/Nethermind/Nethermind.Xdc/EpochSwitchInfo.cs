// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Xdc.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
public class EpochSwitchInfo(Address[] masternodes, Address[] penalties, BlockRoundInfo blockRoundInfo)
{
    public Address[] Masternodes { get; set; } = masternodes;
    public Address[] Penalties { get; set; } = penalties;
    public BlockRoundInfo EpochSwitchBlockInfo { get; set; } = blockRoundInfo;
    public BlockRoundInfo EpochSwitchParentBlockInfo { get; set; }
}
