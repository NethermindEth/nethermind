// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Modules;
using Nethermind.Db;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.State.Flat.Rust.Test;

/// <summary>The world state of a test node on one flat backend, as <see cref="TestNethermindModule"/> builds it.</summary>
internal sealed class WorldStateNode(FlatDbConfig config) : IAsyncDisposable
{
    private readonly IContainer _container = new ContainerBuilder().AddModule(new TestNethermindModule(config)).Build();

    public WorldStateNode(FlatDbBackend backend) : this(FlatManagerContainer.Config(backend))
    {
    }

    public IWorldStateManager WorldStateManager => _container.Resolve<IWorldStateManager>();

    public T Resolve<T>() where T : notnull => _container.Resolve<T>();

    /// <summary>Commits block <paramref name="number"/> on <paramref name="parent"/> through <paramref name="provider"/> with one batch of <paramref name="write"/>.</summary>
    public static BlockHeader Commit(IWorldStateScopeProvider provider, BlockHeader? parent, ulong number, Action<IWorldStateScopeProvider.IWorldStateWriteBatch> write)
    {
        Assert.That(provider.TryBeginScope(parent, new LocalMetrics(), out IWorldStateScopeProvider.IScope? scope), Is.True, $"scope at {parent?.Number}");
        using (scope)
        {
            using (IWorldStateScopeProvider.IWorldStateWriteBatch batch = scope!.StartWriteBatch(1))
            {
                write(batch);
            }

            scope.UpdateRootHash();
            scope.Commit(number);
            return Build.A.BlockHeader.WithNumber(number).WithStateRoot(scope.RootHash).TestObject;
        }
    }

    /// <summary>Sets <paramref name="account"/> at <paramref name="address"/> with the slots of <paramref name="slots"/>, after clearing its storage when asked.</summary>
    public static Action<IWorldStateScopeProvider.IWorldStateWriteBatch> Write(Address address, Account account, bool clearStorage, params (UInt256 Slot, UInt256 Value)[] slots) => batch =>
    {
        batch.Set(address, account);
        using IWorldStateScopeProvider.IStorageWriteBatch storage = batch.CreateStorageWriteBatch(address, slots.Length);
        if (clearStorage) storage.Clear();
        foreach ((UInt256 slot, UInt256 value) in slots) storage.Set(slot, value);
    };

    /// <summary>What <paramref name="reader"/> reads of <paramref name="addresses"/> and <paramref name="slots"/> at <paramref name="header"/>.</summary>
    public static List<string> Reads(IStateReader reader, BlockHeader header, IEnumerable<Address> addresses, UInt256[] slots)
    {
        List<string> reads = [];
        foreach (Address address in addresses)
        {
            reads.Add(reader.TryGetAccount(header, address, out AccountStruct account)
                ? $"account {address} {account.Nonce} {account.Balance} {account.StorageRoot}"
                : $"account {address} none");
            foreach (UInt256 slot in slots)
            {
                reader.GetStorage(header, address, slot, out UInt256 value);
                reads.Add($"slot {address} {slot} {value}");
            }
        }

        return reads;
    }

    public ValueTask DisposeAsync() => _container.DisposeAsync();
}
