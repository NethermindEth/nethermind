// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain;

/// <summary>Whether this node's block producer is executing a block it is building.</summary>
public interface IBlockBuildingTracker
{
    bool IsBuildingBlock { get; }

    /// <summary>Raises <see cref="IsBuildingBlock"/> until the returned scope is disposed.</summary>
    /// <remarks>Builds may overlap, so the flag stays raised until the last open scope is disposed; disposing a
    /// scope twice releases it once.</remarks>
    IDisposable BeginBlockBuilding();
}
