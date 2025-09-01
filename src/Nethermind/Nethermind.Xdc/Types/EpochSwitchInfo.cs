// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System.Collections.Generic;

namespace Nethermind.Consensus.HotStuff.Types;

public class EpochSwitchInfo
{
    public Address[] Penalties { get; set; }
    public Address[] Standbynodes { get; set; }
    public Address[] Masternodes { get; set; }
    public BlockInfo EpochSwitchBlockInfo { get; set; }
    public BlockInfo EpochSwitchParentBlockInfo { get; set; }
}
