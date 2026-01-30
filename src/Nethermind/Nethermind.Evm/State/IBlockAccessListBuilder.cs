// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Evm.State;

public interface IBlockAccessListBuilder
{
    public bool TracingEnabled { get; set; }
    public BlockAccessList GeneratedBlockAccessList { get; set; }
    public void AddAccountRead(Address address);
}
