// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Runtime.CompilerServices;
using Nethermind.Core;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;
using NUnit.Framework;
using CoreCollectionExtensions = Nethermind.Core.Collections.CollectionExtensions;

namespace Nethermind.Store.Test;

public partial class StorageProviderTests
{
    [Test]
    public void Commit_ReadOnlyRounds_ReusesPendingBuffer([Values(2, 2048)] int slotCount)
    {
        using Context ctx = new(UseFlat, preBlockCaches: null);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Get(new StorageCell(ctx.Address1, UInt256.Zero), out _);
        provider.Commit(Frontier.Instance);
        object buffer = null;
        for (int round = 0; round < 3; round++)
        {
            for (int i = 1; i <= slotCount; i++)
            {
                provider.Get(new StorageCell(ctx.Address1, (UInt256)i), out _);
            }

            object currentBuffer = GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues");
            buffer ??= currentBuffer;
            Assert.That(currentBuffer, Is.SameAs(buffer));
            provider.Commit(Frontier.Instance);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), Is.SameAs(buffer));
                Assert.That(((System.Collections.ICollection)buffer).Count, Is.Zero);
            }
        }
    }

    [Test]
    public void Commit_OversizedPendingBuffer_ReleasesBuffer()
    {
        using Context ctx = new(UseFlat, preBlockCaches: null);
        WorldState provider = BuildStorageProvider(ctx);
        provider.Get(new StorageCell(ctx.Address1, UInt256.Zero), out _);
        provider.Commit(Frontier.Instance);
        for (int i = 1; i <= CoreCollectionExtensions.DefaultTrimAboveCapacity + 1; i++)
        {
            provider.Get(new StorageCell(ctx.Address1, (UInt256)i), out _);
        }

        Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), Is.Not.Null);
        provider.Commit(Frontier.Instance);
        Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), Is.Null);
    }

    [Test]
    public void EndScope_PendingBuffer_ReleasedOnlyWhenGrown([Values(1, PersistentStorageProvider.RetainedPendingCapacity + 1)] int slotCount)
    {
        using Context ctx = new(UseFlat, setInitialState: false);
        WorldState provider = BuildStorageProvider(ctx);
        using (provider.BeginScope(IWorldState.PreGenesis))
        {
            provider.Get(new StorageCell(TestItem.AddressA, UInt256.Zero), out _);
            provider.Commit(Frontier.Instance);
            for (int i = 1; i <= slotCount; i++)
            {
                provider.Get(new StorageCell(TestItem.AddressA, (UInt256)i), out _);
            }

            Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), Is.Not.Null);
        }

        bool grown = slotCount > PersistentStorageProvider.RetainedPendingCapacity;
        Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), grown ? Is.Null : Is.Not.Null);
    }

    /// <remarks>Only debug builds report an undisposed list, from its finalizer, so release builds pass trivially.</remarks>
    [Test]
    [NonParallelizable]
    public void EndScope_KeptPendingBuffer_IsNotReportedAsLeaked()
    {
        TextWriter stderr = Console.Error;
        using StringWriter captured = new();
        Console.SetError(captured);
        try
        {
            KeepPendingBufferAndDropProvider();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        finally
        {
            Console.SetError(stderr);
        }

        Assert.That(captured.ToString(), Does.Not.Contain(nameof(PersistentStorageProvider)));

        [MethodImpl(MethodImplOptions.NoInlining)]
        void KeepPendingBufferAndDropProvider()
        {
            using Context ctx = new(UseFlat, setInitialState: false);
            WorldState provider = BuildStorageProvider(ctx);
            using (provider.BeginScope(IWorldState.PreGenesis))
            {
                provider.Get(new StorageCell(TestItem.AddressA, UInt256.Zero), out _);
            }

            Assert.That(GetPrivateField(provider._persistentStorageProvider, "_pendingOriginalValues"), Is.Not.Null);
        }
    }
}
