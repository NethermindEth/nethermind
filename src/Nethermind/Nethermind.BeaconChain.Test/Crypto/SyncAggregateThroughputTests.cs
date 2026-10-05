// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;

namespace Nethermind.BeaconChain.Test.Crypto;

[TestFixture]
[Explicit("Timing comparison; run on demand with --output Detailed")]
public class SyncAggregateThroughputTests
{
    private const int RegistrySize = 1_600_000;
    private const int DistinctKeys = 1024;
    private const int Warmup = 5;
    private const int Replicates = 3;
    private const int IterationsPerReplicate = 30;

    [Test]
    public void Cached_against_decompressed()
    {
        BlsPublicKey[] pool = [.. Enumerable.Range(0, DistinctKeys).Select(static i => new BlsPublicKey(new Bls.P1(ValidatorKey(i)).Compress()))];
        Validator[] registry = new Validator[RegistrySize];
        for (int i = 0; i < registry.Length; i++)
            registry[i] = new Validator { Pubkey = pool[i % DistinctKeys] };
        PubkeyCache pubkeys = new();
        pubkeys.Build(registry);

        Random random = new(7);
        int[] indices = [.. Enumerable.Range(0, Presets.SyncCommitteeSize).Select(_ => random.Next(RegistrySize))];
        BeaconStateFulu state = CreateFuluState(1);
        state.Slot = 1;
        state.CurrentSyncCommittee = new SyncCommittee { Pubkeys = [.. indices.Select(i => registry[i].Pubkey)], AggregatePubkey = Pubkey(0x60) };
        Hash256 signingRoot = Domains.ComputeSigningRoot(state.GetBlockRootAtSlot(0), state.GetDomain(DomainType.SyncCommittee, 0));
        BitArray bits = new(Presets.SyncCommitteeSize, true);
        SyncAggregate syncAggregate = new() { SyncCommitteeBits = bits, SyncCommitteeSignature = AggregateSignature(signingRoot, [.. indices.Select(static i => i % DistinctKeys)]) };

        for (int i = 0; i < Warmup; i++)
        {
            Check(VerifyDecompressing(state, syncAggregate, signingRoot));
            Check(SignatureSets.VerifySyncAggregate(state, syncAggregate, indices, pubkeys));
        }

        for (int replicate = 0; replicate < Replicates; replicate++)
        {
            List<double> decompressedAggregate = [], cachedAggregate = [], uncomparedAggregate = [], decompressedVerify = [], cachedVerify = [];
            for (int i = 0; i < IterationsPerReplicate; i++)
            {
                // Interleaved so drift in clock speed or load lands on both paths alike.
                decompressedAggregate.Add(Time(() => AggregateDecompressing(state.CurrentSyncCommittee.Pubkeys!, bits)));
                cachedAggregate.Add(Time(() => AggregateCached(state.CurrentSyncCommittee.Pubkeys!, bits, indices, pubkeys)));
                uncomparedAggregate.Add(Time(() => AggregateCachedWithoutComparison(bits, indices, pubkeys)));
                decompressedVerify.Add(Time(() => Check(VerifyDecompressing(state, syncAggregate, signingRoot))));
                cachedVerify.Add(Time(() => Check(SignatureSets.VerifySyncAggregate(state, syncAggregate, indices, pubkeys))));
            }

            TestContext.Out.WriteLine(
                $"replicate {replicate}: aggregate decompressed {Median(decompressedAggregate):F3} ms, cached {Median(cachedAggregate):F3} ms, cached without the key comparison {Median(uncomparedAggregate):F3} ms; " +
                $"verify decompressed {Median(decompressedVerify):F3} ms, cached {Median(cachedVerify):F3} ms");
        }
    }

    // Reference aggregation decompresses each participating key without subgroup checks; this isolates cache cost.
    private static void AggregateDecompressing(BlsPublicKey[] committee, BitArray bits)
    {
        BlsSigner.AggregatedPublicKey participants = new(stackalloc long[Bls.P1.Sz]);
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i] && !participants.TryAggregate(committee[i].Bytes, out _))
                throw new InvalidOperationException("fixture bug: a committee key does not decode");
        }
    }

    private static void AggregateCached(BlsPublicKey[] committee, BitArray bits, int[] indices, PubkeyCache pubkeys)
    {
        Bls.P1 participants = new(stackalloc long[Bls.P1.Sz]);
        SignatureSets.AggregateSyncParticipants(bits, committee, indices, pubkeys, participants);
    }

    private static void AggregateCachedWithoutComparison(BitArray bits, int[] indices, PubkeyCache pubkeys)
    {
        Bls.P1 participants = new(stackalloc long[Bls.P1.Sz]);
        for (int i = 0; i < bits.Length; i++)
        {
            if (bits[i])
                participants.Add(pubkeys.GetPublicKey(indices[i]));
        }
    }

    private static bool VerifyDecompressing(BeaconStateFulu state, SyncAggregate syncAggregate, Hash256 signingRoot)
    {
        BlsPublicKey[] committee = state.CurrentSyncCommittee!.Pubkeys!;
        BlsSigner.AggregatedPublicKey participants = new(stackalloc long[Bls.P1.Sz]);
        for (int i = 0; i < syncAggregate.SyncCommitteeBits!.Length; i++)
        {
            if (syncAggregate.SyncCommitteeBits[i] && !participants.TryAggregate(committee[i].Bytes, out _))
                return false;
        }

        return BlockSignatureBatch.Verify(participants.PublicKey, syncAggregate.SyncCommitteeSignature, signingRoot, deferral: null);
    }

    private static void Check(bool valid)
    {
        if (!valid)
            throw new InvalidOperationException("fixture bug: the sync aggregate must verify");
    }

    private static double Time(Action action)
    {
        long start = Stopwatch.GetTimestamp();
        action();
        return Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    }

    private static double Median(List<double> values)
    {
        double[] sorted = [.. values.Order()];
        return sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[sorted.Length / 2 - 1] + sorted[sorted.Length / 2]) / 2;
    }
}
