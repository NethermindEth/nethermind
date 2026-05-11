// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.BlockAccessLists;

namespace Nethermind.Evm.State;

public interface IBlockAccessListSource
{
    BlockAccessList GeneratedBlockAccessList { get; }
}
