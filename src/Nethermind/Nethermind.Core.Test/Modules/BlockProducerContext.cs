// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus;

namespace Nethermind.Core.Test.Modules;

public record BlockProducerContext(
    ILifetimeScope LifetimeScope,
    IBlockProducerRunner BlockProducerRunner
) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await LifetimeScope.DisposeAsync();
    }
}
