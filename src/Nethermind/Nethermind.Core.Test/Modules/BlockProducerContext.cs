// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Autofac;
using Autofac.Features.AttributeFilters;
using Nethermind.Consensus;

namespace Nethermind.Core.Test.Modules;

public record BlockProducerContext(
    ILifetimeScope LifetimeScope,
    [KeyFilter(IBlockProducer.Factory)] IBlockProducer BlockProducer,
    IBlockProducerRunner BlockProducerRunner
) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await LifetimeScope.DisposeAsync();
    }
}
