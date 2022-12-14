// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers;

public class BlockCacheService : IBlockCacheService
{
    public ConcurrentDictionary<Keccak, Block> BlockCache { get; } = new();
    public Keccak FinalizedHash { get; set; } = Keccak.Zero;
}
