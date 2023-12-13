// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public interface IBlockProductionContext
{
    Block? CurrentBestBlock { get; }
    UInt256 BlockFees { get; }
}
