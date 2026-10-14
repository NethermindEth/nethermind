// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs.Forks;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.State.ZkEvm.Test;

public class GuestStorageOriginalsTests
{
    private static readonly Address Contract = new("0x00000000000000000000000000000000000000c0");

    [Test]
    public void Get_ManyReads_IndexesOriginalValuesEagerly()
    {
        WorldState provider = new(HashFreeScopeProvider(), LimboLogs.Instance);
        using IDisposable scope = provider.BeginScope(IWorldState.PreGenesis);
        provider.CreateAccount(Contract, 0);
        provider.Commit(Frontier.Instance);
        for (int i = 1; i <= 2048; i++)
        {
            provider.Get(new StorageCell(Contract, (UInt256)i), out _);
        }

        OptimizedDictionary<StorageCell, UInt256> originals = (OptimizedDictionary<StorageCell, UInt256>)typeof(PersistentStorageProvider)
            .GetField("_originalValues", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(provider._persistentStorageProvider)!;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(originals.Count, Is.EqualTo(2048), "Guest reads must index every original value immediately.");
            Assert.That(typeof(PersistentStorageProvider).GetField("_pendingOriginalValues", BindingFlags.Instance | BindingFlags.NonPublic), Is.Null,
                "The guest must not allocate a deferred capture buffer.");
        }
        provider.Commit(Frontier.Instance);
        Assert.That(originals.Count, Is.Zero, "Commit must clear the guest original-value index.");
    }

    /// <remarks>Keccak is a zkVM syscall the host cannot load, so the backend must never hash.</remarks>
    private static IWorldStateScopeProvider HashFreeScopeProvider()
    {
        IWorldStateScopeProvider.IScope scope = Substitute.For<IWorldStateScopeProvider.IScope>();
        IWorldStateScopeProvider scopeProvider = Substitute.For<IWorldStateScopeProvider>();
        scopeProvider.TryBeginScope(Arg.Any<BlockHeader?>(), Arg.Any<LocalMetrics>(), out Arg.Any<IWorldStateScopeProvider.IScope?>())
            .Returns(call =>
            {
                call[2] = scope;
                return true;
            });
        return scopeProvider;
    }
}
