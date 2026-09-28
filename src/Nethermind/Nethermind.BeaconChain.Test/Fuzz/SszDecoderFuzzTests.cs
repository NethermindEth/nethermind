// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Nethermind.BeaconChain.DataAvailability;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Serialization.Ssz;
using NUnit.Framework;
using static Nethermind.BeaconChain.Test.Types.SignedBeaconBlockBuilders;

namespace Nethermind.BeaconChain.Test.Fuzz;

// A peer controls every byte these decoders see, so any exception other than the documented refusal would escape a network handler.
[Parallelizable(ParallelScope.All)]
public class SszDecoderFuzzTests
{
    private const int ValidEncodingsPerSeed = 4;

    // Past the slot of a block (offset 100) and of a state (offset 40), and past most fixed parts.
    private const int PrefixSweepLength = 256;

    private interface ITarget
    {
        int Iterations { get; }

        byte[][] ValidEncodings(int seed);

        void Decode(byte[] ssz);
    }

    private sealed class Container<T>(int iterations, Action<T>? adjust = null) : ITarget where T : class, ISszCodec<T>
    {
        public int Iterations => iterations;

        public byte[][] ValidEncodings(int seed) => SszFuzzer.ValidEncodings(seed, ValidEncodingsPerSeed, adjust);

        public void Decode(byte[] ssz) => SszFuzzer.RoundTrip<T>(ssz);

        public override string ToString() => typeof(T).Name;
    }

    private static IEnumerable<TestCaseData> Targets()
    {
        ITarget[] targets =
        [
            new Container<SignedBeaconBlock>(150),
            new Container<SignedBeaconBlockGloas>(150),
            new Container<SignedAggregateAndProof>(300),
            new Container<SignedAggregateAndProofGloas>(300),
            new Container<Attestation>(300),
            new Container<AttestationGloas>(300),
            new Container<AttesterSlashing>(300),
            new Container<AttesterSlashingGloas>(300),
            new Container<ProposerSlashing>(300),
            new Container<SignedVoluntaryExit>(300),
            new Container<SignedBlsToExecutionChange>(300),
            new Container<SignedExecutionPayloadEnvelope>(200),
            new Container<SignedExecutionPayloadBid>(300),
            new Container<PayloadAttestationMessage>(300),
            new Container<DataColumnSidecar>(100),
            new Container<DataColumnSidecarGloas>(100),
            new Container<StatusMessageV1>(300),
            new Container<StatusMessageV2>(300),
            new Container<MetaDataV3>(300),
            new Container<BeaconBlocksByRangeRequest>(300),
            new Container<BeaconBlocksByRootRequest>(300),
            new Container<DataColumnSidecarsByRangeRequest>(300),
            new Container<DataColumnSidecarsByRootRequest>(300),
            new Container<ExecutionPayloadEnvelopesByRangeRequest>(300),
            new Container<ExecutionPayloadEnvelopeRoots>(300),
        ];

        foreach (ITarget target in targets)
        {
            foreach (int seed in SszFuzzer.Seeds)
            {
                yield return new TestCaseData(target, seed).SetName($"{target} decodes hostile input or refuses it (seed 0x{seed:x})");
            }
        }
    }

    [TestCaseSource(nameof(Targets))]
    public void Container_decoder_refuses_hostile_input_only_as_invalid_data(object target, int seed)
    {
        ITarget container = (ITarget)target;
        byte[][] valid = container.ValidEncodings(seed);
        foreach (byte[] encoding in valid)
        {
            container.Decode(encoding);
        }

        SszFuzzer.RunPrefixes(valid, PrefixSweepLength, container.Decode, static e => e is InvalidDataException);
        SszFuzzer.Run(seed, valid, container.Iterations, container.Decode, static e => e is InvalidDataException);
    }

    [Test]
    public void Signed_block_codec_decodes_each_fork_shape_or_refuses_by_documented_type([ValueSource(typeof(SszFuzzer), nameof(SszFuzzer.Seeds))] int seed)
    {
        BeaconChainSpec spec = Sepolia;
        byte[][] valid =
        [
            .. SszFuzzer.ValidEncodings<SignedBeaconBlock>(seed, 2, static b => b.Message!.Slot = FirstGloasSlot - 1),
            .. SszFuzzer.ValidEncodings<SignedBeaconBlockGloas>(seed, 2, static b => b.Message!.Slot = FirstGloasSlot),
        ];

        void DecodeAndReencode(byte[] ssz)
        {
            ForkedSignedBeaconBlock block = SignedBeaconBlockCodec.Decode(ssz, spec);
            Assert.That(SignedBeaconBlockCodec.Encode(block, spec).AsSpan().SequenceEqual(ssz), Is.True, "an accepted block encoding must be canonical");
        }

        foreach (byte[] encoding in valid)
        {
            DecodeAndReencode(encoding);
        }

        SszFuzzer.RunPrefixes(valid, PrefixSweepLength, DecodeAndReencode, static e => e is InvalidDataException or BeaconStateException);
        SszFuzzer.Run(seed, valid, 150, DecodeAndReencode, static e => e is InvalidDataException or BeaconStateException);
    }

    [Test]
    public void State_codec_decodes_each_fork_layout_or_refuses_by_documented_type([ValueSource(typeof(SszFuzzer), nameof(SszFuzzer.Seeds))] int seed)
    {
        BeaconChainSpec spec = Sepolia;
        byte[][] valid =
        [
            .. SszFuzzer.ValidEncodings<BeaconStateFulu>(seed, 1, static s => s.Slot = FirstGloasSlot - 1),
            .. SszFuzzer.ValidEncodings<BeaconStateGloas>(seed, 1, static s => s.Slot = FirstGloasSlot),
        ];

        void DecodeAndReencode(byte[] ssz)
        {
            byte[] encoded = BeaconStateCodec.DecodeForked(ssz, spec) switch
            {
                ForkedBeaconState.OfFulu fulu => BeaconStateFulu.Encode(fulu.State),
                ForkedBeaconState.OfGloas gloas => BeaconStateGloas.Encode(gloas.State),
                ForkedBeaconState other => throw new AssertionException($"Unexpected state shape {other.GetType().Name}"),
            };
            Assert.That(encoded.AsSpan().SequenceEqual(ssz), Is.True, "an accepted state encoding must be canonical");
        }

        foreach (byte[] encoding in valid)
        {
            DecodeAndReencode(encoding);
        }

        SszFuzzer.RunPrefixes(valid, PrefixSweepLength, DecodeAndReencode, static e => e is InvalidDataException or BeaconStateException or NotSupportedException);
        SszFuzzer.Run(seed, valid, 12, DecodeAndReencode, static e => e is InvalidDataException or BeaconStateException or NotSupportedException);
    }
}
