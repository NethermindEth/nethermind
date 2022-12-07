// Copyright 2022 Demerzel Solutions Limited
// Licensed under the LGPL-3.0. For full terms, see LICENSE-LGPL in the project root.

using Nethermind.Core;
using Nethermind.Int256;

namespace Nethermind.Merge.Plugin.BlockProduction;

public interface IBlockProductionContext
{
    Block? CurrentBestBlock { get; }
    UInt256 BlockFees { get; }
}
