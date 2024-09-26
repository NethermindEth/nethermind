// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Nethermind.Core.Extensions;
using Nethermind.Core.Verkle;
using Nethermind.Serialization.Json;
using Nethermind.Verkle.Curve;
using Nethermind.Verkle.Fields.FrEElement;
using Nethermind.Verkle.Proofs;
using NUnit.Framework;

namespace Nethermind.Core.Test.Json;

public class ExecutionWitnessConvertorTests
{
    [Test]
    public void TestRoundTripNew()
    {
        Banderwagon[] cl =
        {
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x537ca3dfeff346b5629b940e118251dfef6955e5b65e767860c8ab22aa71c0cb"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x22fb29d39ef04d52c46c04e54eca78c4447beec8530fb906b53b50960e2eab68"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x65500c54a8f544bd326b9b855c3998e4c423e53a5b739a78ab374719313f9b05"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x2bd0dfba832857ea437b6e92af5c70dd3695361d54c55e834ceff0351d8564d6"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x2cf8e3e4b65580979e805a7b457eaca1458007a07f76d2a9d82e2151d83117e9"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x06b8177900aa7d00ff98ff22212171c559dd7087e988d5b435e552e9114e6be0"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x52673e8519921f11ed21b3e3d59f26a3796c571ba5e31bc3b9abd40d7d3141f0"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x45324cd1572a6abe7adc4c6c6c0b4378eb51fd49b39dfaf00a52f4b453ce81ce"))!.Value,
        };

        Banderwagon[] cr =
        {
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x6cfd035a038f140ab7c4c638083fae582f6b5333001d88c14778fed0006cd314"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x368d0d4c6605e511f07470dd992ed919888e5857b7c56cf2c7d399ce51c48538"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x65500c54a8f544bd326b9b855c3998e4c423e53a5b739a78ab374719313f9b05"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x372ee13e677576f7ded1e30f9ff1fe43d28922f7e38bda269a7f1e9baf924475"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x63287515c7aaec7f7729413a0b8786861660ca90fc2ce5df0a598ceeecdccf59"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x739ab678e71885e59b44f473938673a2002ad85a3aad188e1fffdecc143c8dc7"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x248acc46e79ce410ecd151b7c685d3b3156ca37e89015653b55cfcc133852a1f"))!.Value,
            Banderwagon.FromBytes(
                Bytes.FromHexString("0x1e1cf3ba1417175e80e51c2bc3e64c892bc101b54158392e472898fb507ec9ba"))!.Value,
        };
        var frE =
            FrE.FromBytes(
                Bytes.FromHexString("0x13b194a8a18b3fe0d4adb61474b545fef98fab0a971d591a8bdefbb9f743e928"), true);

        var item = new IpaProofStruct(cl, frE, cr);
        var diffs = new StemStateDiff[2];

        var witness = new ExecutionWitness(diffs,
            new WitnessVerkleProof(
                new Stem[] { Stem.MaxValue, Stem.Zero },
                new byte[] { 1, 2 },
                new Banderwagon[]
                {
                    Banderwagon.FromBytes(
                            Bytes.FromHexString("0x248acc46e79ce410ecd151b7c685d3b3156ca37e89015653b55cfcc133852a1f"))!
                        .Value
                },
                Banderwagon.FromBytes(
                    Bytes.FromHexString("0x248acc46e79ce410ecd151b7c685d3b3156ca37e89015653b55cfcc133852a1f"))!.Value,
                item
            ));
        var options = new JsonSerializerOptions
        {
            Converters =
            {
                new IpaProofConverter(),
                new BanderwagonConverter(),
                new ByteArrayConverter(),
                new StemConverter()
            }
        };

        string results = JsonSerializer.Serialize(witness, options);
        ExecutionWitness? res = JsonSerializer.Deserialize<ExecutionWitness>(results, options);
        Assert.That(res, Is.Not.Null);

        // for (int i = 0; i < item.L.Length; i++)
        // {
        //     Assert.IsTrue(item.L[i] == res!.Value.L[i]);
        // }
        //
        // for (int i = 0; i < item.R.Length; i++)
        // {
        //     Assert.IsTrue(item.R[i] == res!.Value.R[i]);
        // }
    }
}
