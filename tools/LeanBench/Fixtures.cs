// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.Tools.LeanBench;

public sealed record WitnessFixture(FrameDependency Dependency, byte[] Witness, int Identity);

public sealed class Fixtures
{
    public WitnessFixture[] Sphincs { get; }
    public WitnessFixture[] Starks { get; }

    public Fixtures(string directory)
    {
        using BinaryReader signatures = new(File.OpenRead(Path.Combine(directory, "sphincs.bin")));
        Sphincs = new WitnessFixture[signatures.ReadInt32()];
        for (int i = 0; i < Sphincs.Length; i++)
        {
            ValueHash256 message = new(Exact(signatures, 32));
            byte[] witness = Exact(signatures, Eip8288Constants.LeanSphincsWitnessBytes);
            ValueHash256 publicKey = ValueKeccak.Compute(witness.AsSpan(0, 32));
            Sphincs[i] = new(new(Eip8288Constants.LeanSphincsScheme, message, publicKey), witness, i);
        }
        using BinaryReader starks = new(File.OpenRead(Path.Combine(directory, "starks.bin")));
        Starks = new WitnessFixture[starks.ReadInt32()];
        for (int i = 0; i < Starks.Length; i++)
        {
            ValueHash256 message = new(Exact(starks, 32));
            ValueHash256 key = new(Exact(starks, 32));
            Starks[i] = new(new(Eip8288Constants.LeanStarkScheme, message, key), Exact(starks, starks.ReadInt32()), i);
        }
    }

    private static byte[] Exact(BinaryReader reader, int length)
    {
        if (length < 0 || length > Eip8288Constants.MaxProofBytes) throw new InvalidDataException("Fixture size outside bounds");
        byte[] bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new EndOfStreamException("Truncated proof fixture");
        return bytes;
    }

    public AggregationInput Input(BenchCase scenario, int index)
    {
        List<FrameDependency> deps = [];
        List<ReadOnlyMemory<byte>> witnesses = [];
        for (int i = 0; i < scenario.SphincsCount; i++)
        {
            WitnessFixture fixture = Sphincs[checked(index * scenario.SphincsCount + i)];
            deps.Add(fixture.Dependency);
            witnesses.Add(fixture.Witness);
        }
        if (scenario.StarkCount > 0)
        {
            WitnessFixture fixture = Starks[index];
            deps.Add(fixture.Dependency);
            witnesses.Add(fixture.Witness);
        }
        if (Eip8288Dependencies.Canonicalize(deps).Count != deps.Count)
            throw new InvalidOperationException("Benchmark claims must be distinct");
        return new() { Deps = deps, Witnesses = witnesses };
    }
}
