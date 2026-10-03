// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Autofac;
using Nethermind.TxPool;

namespace Nethermind.Core.Test.Modules;

/// <summary>
/// Resolves the MATCHA width ledger from the production modules for tests that construct a
/// <see cref="TxPool.TxPool"/> by hand.
/// </summary>
public static class TestFrameTxWidthLedger
{
    /// <summary>Returns the module-registered ledger, bound to <paramref name="txPoolConfig"/>.</summary>
    public static FrameTxWidthLedger For(ITxPoolConfig txPoolConfig)
    {
        using IContainer container = new ContainerBuilder()
            .AddModule(new TestNethermindModule(txPoolConfig))
            .Build();

        return container.Resolve<FrameTxWidthLedger>();
    }
}
