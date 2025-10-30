// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Collections;

namespace Nethermind.Merge.Plugin.Handlers;

public class BlockCacheService : IBlockCacheService
{
    public ConcurrentDictionary<ComparableBox<Hash256>, Block> BlockCache { get; } = new();
    public Hash256? FinalizedHash { get; set; }
    public Hash256? HeadBlockHash { get; set; }
}
