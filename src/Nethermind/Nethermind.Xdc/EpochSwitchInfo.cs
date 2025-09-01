// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Xdc;
internal class EpochSwitchInfo
{
    public Address[] Penalties { get; set; }
    public Address[] Masternodes { get; set; }
    public BlockInfo EpochSwitchBlockInfo { get; set; }
    public BlockInfo EpochSwitchParentBlockInfo { get; set; }
}
