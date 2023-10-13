// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Concurrent;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Merge.Plugin.Handlers;

public interface IBlockCacheService
{
    public ConcurrentDictionary<Commitment, Block> BlockCache { get; }
    Commitment FinalizedHash { get; set; }
}
