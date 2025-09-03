// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Nethermind.Consensus.Clique;
public class MasterNode(Address address, UInt256 stake)
{
    public Address Address { get; set; } = address;
    public UInt256 Stake { get; set; } = stake;
}
