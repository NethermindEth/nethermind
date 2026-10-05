// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public sealed class BlockBuildingTracker : IBlockBuildingTracker
{
    private int _buildingBlocks;

    public bool IsBuildingBlock => Volatile.Read(ref _buildingBlocks) > 0;

    public IDisposable BeginBlockBuilding()
    {
        Interlocked.Increment(ref _buildingBlocks);
        return new Reactive.AnonymousDisposable(() => Interlocked.Decrement(ref _buildingBlocks));
    }
}
