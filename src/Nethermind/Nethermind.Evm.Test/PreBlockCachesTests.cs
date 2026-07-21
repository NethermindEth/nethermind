// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Int256;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

public class PreBlockCachesTests
{
    [Test]
    public async Task Concurrent_storage_misses_are_coalesced()
    {
        PreBlockCaches caches = new();
        StorageCell storageCell = new(Address.Zero, UInt256.One);
        using LoadState state = new();

        Task<(byte[]? Value, bool CacheHit)> first = Load();
        Assert.That(state.Started.Wait(TimeSpan.FromSeconds(5)), Is.True);

        Task<(byte[]? Value, bool CacheHit)> second = Load();
        state.Release.Set();

        (byte[]? firstValue, bool firstCacheHit) = await first;
        (byte[]? secondValue, bool secondCacheHit) = await second;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.LoadCount, Is.EqualTo(1));
            Assert.That(firstCacheHit, Is.False);
            Assert.That(secondCacheHit, Is.True);
            Assert.That(firstValue, Is.SameAs(state.Value));
            Assert.That(secondValue, Is.SameAs(state.Value));
        }

        Task<(byte[]? Value, bool CacheHit)> Load() => Task.Run(() =>
        {
            byte[]? value = caches.GetOrAddStorage(
                in storageCell,
                state,
                static (in StorageCell cell, LoadState loadState) => loadState.Load(in cell),
                out bool cacheHit);
            return (value, cacheHit);
        });
    }

    private sealed class LoadState : IDisposable
    {
        public readonly ManualResetEventSlim Started = new();
        public readonly ManualResetEventSlim Release = new();
        public readonly byte[] Value = [1, 2, 3];

        public int LoadCount;

        public byte[] Load(in StorageCell _)
        {
            Interlocked.Increment(ref LoadCount);
            Started.Set();
            Release.Wait();
            return Value;
        }

        public void Dispose()
        {
            Started.Dispose();
            Release.Dispose();
        }
    }
}
