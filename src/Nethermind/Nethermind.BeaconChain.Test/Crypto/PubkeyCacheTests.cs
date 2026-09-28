// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Storage;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Db;
using NUnit.Framework;

namespace Nethermind.BeaconChain.Test.Crypto;

public class PubkeyCacheTests
{
    private static readonly byte[] MasterSkBytes = Bytes.FromHexString("0x2cd4ba406b522459d57a0bed51a397435c0bb11dd5f3ca1152b3694bb91d7c22");

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

    [Test]
    public void Build_throws_with_index_of_invalid_pubkey()
    {
        Validator[] validators =
        [
            new Validator { Pubkey = new BlsPublicKey(CompressedPubkey(0)) },
            new Validator { Pubkey = new BlsPublicKey(CompressedPubkey(1)) },
            new Validator { Pubkey = new BlsPublicKey(Bytes.FromHexString("0xffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff")) },
        ];

        Assert.That(
            () => new PubkeyCache().Build(validators),
            Throws.InvalidOperationException.With.Message.Contains("Validator 2"));
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
        Bls.P1 publicKey = new(new Bls.SecretKey(new Bls.SecretKey(MasterSkBytes, Bls.ByteOrder.LittleEndian), (uint)index));
        return publicKey.Compress();
    }
}
