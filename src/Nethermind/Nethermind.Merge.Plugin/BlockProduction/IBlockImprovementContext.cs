// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Merge.Plugin.BlockProduction;

public interface IBlockImprovementContext : IBlockProductionContext, IDisposable
{
    Task<Block?> ImprovementTask { get; }
    bool Disposed { get; }
    DateTimeOffset StartDateTime { get; }
}
