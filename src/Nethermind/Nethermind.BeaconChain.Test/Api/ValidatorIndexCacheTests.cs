// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using Nethermind.BeaconChain.Api.Common;
using Nethermind.BeaconChain.Types;

namespace Nethermind.BeaconChain.Test.Api;

public class ValidatorIndexCacheTests
{
    [Test]
    public async Task Concurrent_requests_share_one_index_for_each_registry()
    {
        ValidatorIndexCache cache = new();
        Validator[] validators = [new() { Pubkey = BeaconApiTestHost.FilledPubkey(1) }];
        Dictionary<BlsPublicKey, int>[] indices = await Task.WhenAll(
            Enumerable.Range(0, 32).Select(_ => Task.Run(() => cache.Get(validators))));

        using IDisposable scope = Assert.EnterMultipleScope();
        foreach (Dictionary<BlsPublicKey, int> index in indices)
            Assert.That(index, Is.SameAs(indices[0]));
        Assert.That(indices[0][validators[0].Pubkey], Is.Zero);
        Validator[] replacement = [new() { Pubkey = BeaconApiTestHost.FilledPubkey(2) }];
        Assert.That(cache.Get(replacement).ContainsKey(validators[0].Pubkey), Is.False);
        Assert.That(cache.Get(replacement)[replacement[0].Pubkey], Is.Zero);
        Assert.That(cache.Get(validators), Is.SameAs(indices[0]));
    }

    [Test]
    public void Expired_registries_are_not_retained()
    {
        ValidatorIndexCache cache = new();
        WeakReference registry = IndexTemporaryRegistry(cache);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Assert.That(registry.IsAlive, Is.False);
        GC.KeepAlive(cache);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference IndexTemporaryRegistry(ValidatorIndexCache cache)
    {
        Validator[] validators = [new() { Pubkey = BeaconApiTestHost.FilledPubkey(1) }];
        cache.Get(validators);
        return new WeakReference(validators);
    }
}
