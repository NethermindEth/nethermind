// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Consensus.Producers;

public interface IBlockImprovementContext : IDisposable
{
    /// <summary>The best candidate produced so far, replaced as a whole when a better block is built.</summary>
    BlockProductionSnapshot Best { get; }
    Task<Block?> ImprovementTask { get; }
    bool Disposed { get; }
    DateTimeOffset StartDateTime { get; }
    void CancelOngoingImprovements();

    void DisposeAndCancelOngoingImprovements()
    {
        CancelOngoingImprovements();
        Dispose();
    }
}
