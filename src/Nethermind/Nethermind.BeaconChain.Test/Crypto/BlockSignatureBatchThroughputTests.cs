// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.BeaconChain.Crypto;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.StateTransition.GloasTestFixtures;
using G1Affine = Nethermind.Crypto.Bls.P1Affine;

namespace Nethermind.BeaconChain.Test.Crypto;

/// <summary>
/// Wall-clock comparison of serial against batched verification of one mainnet slot's block signatures.
/// </summary>
/// <remarks>
/// Each set carries one public key: an aggregate verifies at the same cost as a single key once its
/// keys are summed, and that summing is the same work on both paths, so it is left out. The proposer
/// signature, which is verified before the batch, is not in either mix.
/// </remarks>
[TestFixture]
[Explicit("Timing comparison; run on demand with --output Detailed")]
public class BlockSignatureBatchThroughputTests
{
    private const int Warmup = 5;
    private const int Replicates = 3;
    private const int IterationsPerReplicate = 30;

    private readonly record struct SignatureCheck(long[] PublicKey, BlsSignature Signature, Hash256 Message);

    // RANDAO, the Electra attestation limit, the sync aggregate, a builder bid and the PTC aggregate limit.
    private static readonly int TypicalSlot = 1 + Presets.MaxAttestationsElectra + 1 + 1 + Presets.MaxPayloadAttestations;

    // A typical slot plus a proposer slashing, an attester slashing, and full exit and BLS change lists.
    private static readonly int FullSlot = TypicalSlot + 2 + 2 * Presets.MaxAttesterSlashingsElectra + Presets.MaxVoluntaryExits + Presets.MaxBlsToExecutionChanges;

    private static IEnumerable<TestCaseData> Mixes()
    {
        yield return new TestCaseData(TypicalSlot).SetName($"typical_slot_{TypicalSlot}_sets");
        yield return new TestCaseData(FullSlot).SetName($"full_slot_{FullSlot}_sets");
    }

    [TestCaseSource(nameof(Mixes))]
    public void Serial_against_batched(int setCount)
    {
        SignatureCheck[] checks = CreateChecks(setCount);
        for (int i = 0; i < Warmup; i++)
        {
            VerifySerially(checks);
            VerifyBatched(checks);
        }

        for (int replicate = 0; replicate < Replicates; replicate++)
        {
            List<double> serial = [];
            List<double> batched = [];
            for (int i = 0; i < IterationsPerReplicate; i++)
            {
                // Interleaved so drift in clock speed or load lands on both paths alike.
                serial.Add(Time(() => VerifySerially(checks)));
                batched.Add(Time(() => VerifyBatched(checks)));
            }

            double serialMedian = Median(serial);
            double batchedMedian = Median(batched);
            TestContext.Out.WriteLine(
                $"{setCount} sets, replicate {replicate}: serial {serialMedian:F3} ms, batched {batchedMedian:F3} ms, " +
                $"batched/serial {batchedMedian / serialMedian:F3}");
        }
    }

    private static SignatureCheck[] CreateChecks(int count)
    {
        SignatureCheck[] checks = new SignatureCheck[count];
        for (int i = 0; i < count; i++)
        {
            Hash256 message = Keccak.Compute([(byte)i, (byte)(i >> 8)]);
            checks[i] = new SignatureCheck(new Bls.P1(DeriveKey(5000 + i)).ToAffine().Point.ToArray(), Sign(DeriveKey(5000 + i), message), message);
        }

        return checks;
    }

    private static void VerifySerially(SignatureCheck[] checks)
    {
        foreach (SignatureCheck check in checks)
        {
            if (!BlockSignatureBatch.Verify(new G1Affine(check.PublicKey), check.Signature, check.Message, deferral: null))
                throw new InvalidOperationException("fixture bug: a serial check failed");
        }
    }

    private static void VerifyBatched(SignatureCheck[] checks)
    {
        BlockSignatureBatch batch = new();
        foreach (SignatureCheck check in checks)
        {
            if (!BlockSignatureBatch.Verify(new G1Affine(check.PublicKey), check.Signature, check.Message, batch.Defer("timing")))
                throw new InvalidOperationException("fixture bug: a check was refused on the spot");
        }

        batch.Verify();
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
