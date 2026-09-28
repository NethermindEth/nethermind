// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
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

    private static byte[] CompressedPubkey(int index)
    {
        Bls.P1 publicKey = new(SecretKey(index));
        return publicKey.Compress();
    }

    private static Bls.SecretKey SecretKey(int index) => new(new Bls.SecretKey(MasterSkBytes, Bls.ByteOrder.LittleEndian), (uint)index);
}
