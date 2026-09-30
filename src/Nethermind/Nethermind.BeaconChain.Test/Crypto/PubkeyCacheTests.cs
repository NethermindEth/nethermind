// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Db;
using NUnit.Framework;
using Snappier;

namespace Nethermind.BeaconChain.Test.Crypto;

public class PubkeyCacheTests
{
    private static readonly byte[] MasterSkBytes = Bytes.FromHexString("0x2cd4ba406b522459d57a0bed51a397435c0bb11dd5f3ca1152b3694bb91d7c22");

    // The store key of the first persisted chunk, which holds validators 0 to 65535.
    private const string FirstChunkKey = "pubkeys:0";

    [Test]
    public void Builds_extends_persists_and_loads_real_pubkeys()
    {
        byte[][] compressed = [.. Enumerable.Range(0, 3).Select(CompressedPubkey)];
        Validator[] validators = [.. compressed.Select(static pk => new Validator { Pubkey = new BlsPublicKey(pk) })];
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());

        PubkeyCache cache = new();
        cache.Build(validators[..2]);
        cache.Extend(validators, 2);
        cache.Persist(store);

        PubkeyCache loaded = new();
        bool loadResult = loaded.TryLoad(store, validators);

        PubkeyCache mismatched = new();
        bool mismatchedResult = mismatched.TryLoad(store, validators[..2]);

        Assert.Multiple(() =>
        {
            Assert.That(cache.Count, Is.EqualTo(3));
            Assert.That(loadResult, Is.True);
            Assert.That(loaded.Count, Is.EqualTo(3));
            for (int i = 0; i < validators.Length; i++)
            {
                Assert.That(cache.GetPublicKey(i).Compress(), Is.EqualTo(compressed[i]), $"built pubkey {i}");
                Assert.That(loaded.GetPublicKey(i).Compress(), Is.EqualTo(compressed[i]), $"loaded pubkey {i}");
            }
            Assert.That(mismatchedResult, Is.False, "count mismatch must force a rebuild");
        });
    }

    /// <summary>
    /// An infinity key would pass unnoticed through every aggregate summed from the cache, which <c>KeyValidate</c> in
    /// <c>FastAggregateVerify</c> refuses, so the cache refuses it as it refuses a key that does not decode.
    /// </summary>
    [Test]
    public void Build_and_extend_throw_with_index_of_a_refused_pubkey([Values("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff", "0xc00000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000000")] string refused, [Values] bool extend)
    {
        Validator[] validators =
        [
            new Validator { Pubkey = new BlsPublicKey(CompressedPubkey(0)) },
            new Validator { Pubkey = new BlsPublicKey(CompressedPubkey(1)) },
            new Validator { Pubkey = new BlsPublicKey(Bytes.FromHexString(refused)) },
        ];
        PubkeyCache cache = new();
        Action cacheAll = () => cache.Build(validators);
        if (extend)
        {
            cache.Build(validators[..2]);
            cacheAll = () => cache.Extend(validators, 2);
        }

        Assert.That(cacheAll, Throws.InvalidOperationException.With.Message.Contains("Validator 2"));
    }

    /// <summary>A persisted buffer holding the point at infinity is not loaded, so the rebuild refuses the registry.</summary>
    [Test]
    public void A_persisted_infinity_point_is_not_loaded()
    {
        Validator[] validators = [.. Enumerable.Range(0, 3).Select(static i => new Validator { Pubkey = new BlsPublicKey(CompressedPubkey(i)) })];
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        PubkeyCache cache = new();
        cache.Build(validators);
        cache.Persist(store);

        // blst reads an all-zero affine point as infinity; the middle entry escapes the first-and-last sample check.
        int pointBytes = Bls.P1Affine.Sz * sizeof(long);
        byte[] points = Snappy.DecompressToArray(store.GetMetadata(FirstChunkKey)!);
        points.AsSpan(pointBytes, pointBytes).Clear();
        store.PutMetadata(FirstChunkKey, Snappy.CompressToArray(points));

        PubkeyCache loaded = new();
        Assert.That(loaded.TryLoad(store, validators), Is.False);
        Assert.That(loaded.Count, Is.Zero);
    }

    /// <summary>The subgroup check is remembered per validator, and a remembered result survives extending the cache.</summary>
    [Test]
    public void Subgroup_checks_are_remembered_across_calls_and_extension()
    {
        Validator[] validators =
        [
            new Validator { Pubkey = new BlsPublicKey(CompressedPubkey(0)) },
            new Validator { Pubkey = OffSubgroupKeys.WithTorsion(SecretKey(1)) },
            new Validator { Pubkey = new BlsPublicKey(CompressedPubkey(2)) },
        ];
        PubkeyCache cache = new();
        cache.Build(validators[..2]);
        bool[] first = [cache.IsInSubgroup(0), cache.IsInSubgroup(1)];
        cache.Extend(validators, 2);
        bool[] second = [cache.IsInSubgroup(0), cache.IsInSubgroup(1), cache.IsInSubgroup(2)];

        Assert.That(first, Is.EqualTo(new[] { true, false }));
        Assert.That(second, Is.EqualTo(new[] { true, false, true }));
    }

    /// <summary>
    /// A cache restored at start-up checks subgroups as a built one does, and re-caching an index forgets the result
    /// remembered for the key it held before; otherwise a stale verdict decides aggregates over the new key.
    /// </summary>
    [Test]
    public void Subgroup_checks_follow_a_loaded_cache_and_a_recached_key()
    {
        Validator[] validators =
        [
            new Validator { Pubkey = new BlsPublicKey(CompressedPubkey(0)) },
            new Validator { Pubkey = OffSubgroupKeys.WithTorsion(SecretKey(1)) },
        ];
        BeaconChainStore store = new(new MemColumnsDb<BeaconChainDbColumns>());
        PubkeyCache cache = new();
        cache.Build(validators);
        cache.Persist(store);
        PubkeyCache loaded = new();
        bool loadResult = loaded.TryLoad(store, validators);
        bool[] loadedChecks = [loaded.IsInSubgroup(0), loaded.IsInSubgroup(1)];

        bool before = cache.IsInSubgroup(1);
        cache.Extend([validators[0], new Validator { Pubkey = new BlsPublicKey(CompressedPubkey(1)) }], 1);

        Assert.That(loadResult, Is.True);
        Assert.That(loadedChecks, Is.EqualTo(new[] { true, false }));
        Assert.That((before, cache.IsInSubgroup(1)), Is.EqualTo((false, true)));
    }

    /// <summary>
    /// A block import that finds a remembered verdict does no subgroup work, so warming must leave a verdict for every key,
    /// including the ones outside G1, and must stop when cancelled.
    /// </summary>
    [Test]
    public void Warming_remembers_the_verdict_of_every_key_and_can_be_cancelled()
    {
        Validator[] validators = MixedRegistry(30);
        PubkeyCache cache = new();
        cache.Build(validators);

        Assert.That(() => cache.WarmSubgroupChecks(new CancellationToken(canceled: true)), Throws.InstanceOf<OperationCanceledException>());
        Assert.That(Enumerable.Range(0, validators.Length).Any(cache.HasSubgroupCheck), Is.False, "a cancelled warm-up checks nothing");

        cache.WarmSubgroupChecks(CancellationToken.None);

        Assert.That(Enumerable.Range(0, validators.Length).All(cache.HasSubgroupCheck), Is.True);
        Assert.That(Enumerable.Range(0, validators.Length).Select(cache.IsInSubgroup), Is.EqualTo(Enumerable.Range(0, validators.Length).Select(static i => !IsOffSubgroup(i))));
    }

    /// <summary>A block import that overlaps the warm-up must get the right verdict for every key, whether it computes it or reads the warm-up's.</summary>
    [Test]
    public async Task Readers_racing_the_warm_up_get_the_right_verdict_for_every_key()
    {
        Validator[] validators = MixedRegistry(1_500);
        PubkeyCache cache = new();
        cache.Build(validators);
        using ManualResetEventSlim start = new();

        Task warm = Task.Run(() => { start.Wait(); cache.WarmSubgroupChecks(CancellationToken.None); });
        Task<int>[] readers = [.. Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
        {
            start.Wait();
            int wrong = 0;
            do
            {
                for (int n = 0; n < validators.Length; n++)
                {
                    int i = reader % 2 == 0 ? n : validators.Length - 1 - n;
                    if (cache.IsInSubgroup(i) == IsOffSubgroup(i))
                        wrong++;
                }
            }
            while (!warm.IsCompleted);
            return wrong;
        }))];
        start.Set();

        await warm;
        Assert.That(await Task.WhenAll(readers), Is.All.Zero);
        Assert.That(Enumerable.Range(0, validators.Length).All(cache.HasSubgroupCheck), Is.True);
    }

    /// <summary>Validators appended while the warm-up runs must not cost it the verdicts of the keys it already covered, or the imports that follow repeat those checks inline.</summary>
    [Test]
    public void A_registry_extension_during_the_warm_up_keeps_every_verdict_of_the_original_keys()
    {
        const int original = DistinctKeys;
        Validator[] validators = CycledRegistry(original + 1);
        PubkeyCache cache = new();
        cache.Build(validators[..original]);
        bool extended = false;
        // The first pass then stores its verdicts in the array the extension has already copied and replaced.
        cache.WarmUpPassStarted = () =>
        {
            if (!extended)
            {
                extended = true;
                cache.Extend(validators, original);
            }
        };

        cache.WarmSubgroupChecks(CancellationToken.None);

        AssertEveryVerdictRemembered(cache, original);
    }

    /// <summary>A warm-up that ends while a long extension is still decoding must still find its verdicts in the extended cache.</summary>
    [Test]
    public async Task A_warm_up_that_ends_inside_a_registry_extension_keeps_every_verdict_of_the_original_keys()
    {
        const int original = DistinctKeys;
        Validator[] validators = CycledRegistry(original + 1);
        PubkeyCache cache = new();
        cache.Build(validators[..original]);
        using ManualResetEventSlim decoded = new();
        using ManualResetEventSlim release = new();
        cache.ExtensionDecoded = () =>
        {
            decoded.Set();
            release.Wait();
        };

        Task extend = Task.Factory.StartNew(() => cache.Extend(validators, original), TaskCreationOptions.LongRunning);
        try
        {
            Assert.That(decoded.Wait(TimeSpan.FromSeconds(30)), Is.True, "the extension holds before it publishes");
            cache.WarmSubgroupChecks(CancellationToken.None);
        }
        finally
        {
            release.Set();
        }
        await extend;

        AssertEveryVerdictRemembered(cache, original);
    }

    private const int DistinctKeys = 64;

    /// <summary>A registry that repeats <see cref="MixedRegistry"/> keys, so a large one costs no key generation.</summary>
    private static Validator[] CycledRegistry(int count)
    {
        Validator[] keys = MixedRegistry(DistinctKeys);
        return [.. Enumerable.Range(0, count).Select(i => keys[i % DistinctKeys])];
    }

    private static void AssertEveryVerdictRemembered(PubkeyCache cache, int original)
    {
        Assert.That(Enumerable.Range(0, original).All(cache.HasSubgroupCheck), Is.True, "no original key is left to an inline check");
        Assert.That(Enumerable.Range(0, original).Select(cache.IsInSubgroup), Is.EqualTo(Enumerable.Range(0, original).Select(static i => !IsOffSubgroup(i % DistinctKeys))));
    }

    /// <summary>The batched sum must be the same point the per-key aggregation produces, or every aggregate attestation check changes meaning.</summary>
    [TestCase(1, 1)]
    [TestCase(2, 1)]
    [TestCase(160, 3)]
    [TestCase(300, 1)]
    public void Summed_public_keys_equal_the_one_at_a_time_aggregate(int count, int stride)
    {
        Validator[] validators = [.. Enumerable.Range(0, 600).Select(static i => new Validator { Pubkey = new BlsPublicKey(CompressedPubkey(i)) })];
        PubkeyCache cache = new();
        cache.Build(validators);
        ulong[] indices = [.. Enumerable.Range(0, count).Select(i => (ulong)(i * stride + 7))];

        BlsSigner.AggregatedPublicKey oneAtATime = new(new long[Bls.P1.Sz]);
        foreach (ulong index in indices)
        {
            oneAtATime.Aggregate(cache.GetPublicKey((int)index));
        }

        long[] sum = new long[Bls.P1.Sz];
        cache.SumPublicKeys(indices, sum);

        Assert.That(new BlsSigner.AggregatedPublicKey(sum).PublicKey.Compress(), Is.EqualTo(oneAtATime.PublicKey.Compress()));
    }

    [Test]
    public void Summing_an_index_past_the_cache_throws()
    {
        PubkeyCache cache = new();
        cache.Build([new Validator { Pubkey = new BlsPublicKey(CompressedPubkey(0)) }]);

        Assert.Throws<System.ArgumentOutOfRangeException>(() => cache.SumPublicKeys([0, 1], new long[Bls.P1.Sz]));
    }

    private static bool IsOffSubgroup(int index) => index % 7 == 3;

    /// <summary>A registry of distinct keys where every seventh one is outside G1.</summary>
    private static Validator[] MixedRegistry(int count) =>
        [.. Enumerable.Range(0, count).Select(static i => new Validator { Pubkey = IsOffSubgroup(i) ? OffSubgroupKeys.WithTorsion(SecretKey(i)) : new BlsPublicKey(CompressedPubkey(i)) })];

    private static byte[] CompressedPubkey(int index)
    {
        Bls.P1 publicKey = new(SecretKey(index));
        return publicKey.Compress();
    }

    private static Bls.SecretKey SecretKey(int index) => new(new Bls.SecretKey(MasterSkBytes, Bls.ByteOrder.LittleEndian), (uint)index);
}
