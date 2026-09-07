// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Consensus.Producers;

public interface IBlockImprovementContext : IBlockProductionContext, IDisposable
{
    Task<Block?> ImprovementTask { get; }
    bool Disposed { get; }
    DateTimeOffset StartDateTime { get; }

    /// <summary>Returns the current best block and its fees as one immutable pair.</summary>
    IBlockProductionContext Snapshot();

    void CancelOngoingImprovements();

    void DisposeAndCancelOngoingImprovements()
    {
        CancelOngoingImprovements();
        Dispose();
    }
}
