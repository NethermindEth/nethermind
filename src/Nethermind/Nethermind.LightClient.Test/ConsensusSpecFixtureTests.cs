// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Security.Cryptography;
using System.Text.Json;
using Nethermind.BeaconChain.Spec;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Ssz;
using Snappier;

namespace Nethermind.LightClient.Test;

[TestFixture]
public class ConsensusSpecFixtureTests
{
    // Unmodified SSZ from consensus-specs v1.7.0-beta.4 at aa16bb4c156184e9548a997d53efaf2a228a6304.
    // In mainnet.tar.gz, use tests/mainnet/{electra,fulu,gloas}/ssz_static/LightClient*/ssz_random/case_0/serialized.ssz_snappy.
    // expected.json pins file digests, roots.yaml roots and selected value.yaml fields.
    [Test]
    public void Official_message_roots_fields_and_wire_bytes_match(
        [Values("electra", "fulu", "gloas")] string fork,
        [Values("bootstrap", "update", "finality", "optimistic")] string shape)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "TestData", "ConsensusSpec");
        using JsonDocument metadata = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "expected.json")));
        JsonElement expected = metadata.RootElement.GetProperty(fork + "/" + shape);
        byte[] compressed = File.ReadAllBytes(Path.Combine(directory, fork, shape + ".ssz_snappy"));
        Assert.That(Convert.ToHexString(SHA256.HashData(compressed)), Is.EqualTo(expected.GetProperty("sha256").GetString()).IgnoreCase);
        byte[] canonical = Snappy.DecompressToArray(compressed);
        Hash256 expectedRoot = new(expected.GetProperty("root").GetString()!);
        bool gloas = fork == "gloas";
        // The random vector slots require selecting the wire fork explicitly.
        BeaconChainSpec spec = BeaconChainSpec.Mainnet with { GloasForkEpoch = gloas ? 0UL : ulong.MaxValue };
        LightClientHeader header;
        ulong? signatureSlot = null;
        byte[] encoded;
        switch (shape)
        {
            case "bootstrap":
                AssertRoot<LightClientBootstrap, GloasLightClientBootstrap>(canonical, expectedRoot, gloas);
                LightClientBootstrap bootstrap = LightClientWireCodec.DecodeBootstrap(canonical, spec);
                header = bootstrap.Header!;
                encoded = LightClientWireCodec.EncodeBootstrap(bootstrap, spec);
                break;
            case "update":
                AssertRoot<LightClientUpdate, GloasLightClientUpdate>(canonical, expectedRoot, gloas);
                LightClientUpdate update = LightClientWireCodec.DecodeUpdate(canonical, spec);
                header = update.AttestedHeader!;
                signatureSlot = update.SignatureSlot;
                encoded = LightClientWireCodec.EncodeUpdate(update, spec);
                break;
            case "finality":
                AssertRoot<LightClientFinalityUpdate, GloasLightClientFinalityUpdate>(canonical, expectedRoot, gloas);
                LightClientFinalityUpdate finality = LightClientWireCodec.DecodeFinality(canonical, spec);
                header = finality.AttestedHeader!;
                signatureSlot = finality.SignatureSlot;
                encoded = LightClientWireCodec.EncodeFinality(finality, spec);
                break;
            case "optimistic":
                AssertRoot<LightClientOptimisticUpdate, GloasLightClientOptimisticUpdate>(canonical, expectedRoot, gloas);
                LightClientOptimisticUpdate optimistic = LightClientWireCodec.DecodeOptimistic(canonical, spec);
                header = optimistic.AttestedHeader!;
                signatureSlot = optimistic.SignatureSlot;
                encoded = LightClientWireCodec.EncodeOptimistic(optimistic, spec);
                break;
            default: throw new ArgumentException("Unknown fixture shape.", nameof(shape));
        }
        JsonElement beacon = expected.GetProperty("beacon");
        Assert.That(header.Beacon, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Is.EqualTo(canonical));
            Assert.That(header.IsGloas, Is.EqualTo(gloas));
            Assert.That(header.Beacon!.Slot, Is.EqualTo(ulong.Parse(beacon.GetProperty("slot").GetString()!)));
            Assert.That(header.Beacon.ProposerIndex, Is.EqualTo(ulong.Parse(beacon.GetProperty("proposer_index").GetString()!)));
            Assert.That(header.Beacon.ParentRoot, Is.EqualTo(new Hash256(beacon.GetProperty("parent_root").GetString()!)));
            Assert.That(header.Beacon.StateRoot, Is.EqualTo(new Hash256(beacon.GetProperty("state_root").GetString()!)));
            Assert.That(header.Beacon.BodyRoot, Is.EqualTo(new Hash256(beacon.GetProperty("body_root").GetString()!)));
            Assert.That(header.ExecutionBlockHash ?? header.Execution?.BlockHash,
                Is.EqualTo(new Hash256(expected.GetProperty("execution_block_hash").GetString()!)));
            if (signatureSlot is not null)
                Assert.That(signatureSlot.Value, Is.EqualTo(ulong.Parse(expected.GetProperty("signature_slot").GetString()!)));
        }
    }

    private static void AssertRoot<TLegacy, TGloas>(byte[] canonical, Hash256 expected, bool gloas)
        where TLegacy : class, ISszCodec<TLegacy>
        where TGloas : class, ISszCodec<TGloas>
    {
        Hash256 root;
        if (gloas)
        {
            TGloas.Decode(canonical, out TGloas value);
            root = SszRoots.HashTreeRoot(value);
        }
        else
        {
            TLegacy.Decode(canonical, out TLegacy value);
            root = SszRoots.HashTreeRoot(value);
        }
        Assert.That(root, Is.EqualTo(expected), "Independent upstream SSZ root");
    }
}
