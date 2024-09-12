// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Consensus.Processing;

public interface IBlockCachePreWarmer
{
    Task PreWarmCaches(Block suggestedBlock, Hash256 parentStateRoot, Address? beaconRootsAddress, CancellationToken cancellationToken = default);
    void ClearCaches();
    Task ClearCachesInBackground();
}
