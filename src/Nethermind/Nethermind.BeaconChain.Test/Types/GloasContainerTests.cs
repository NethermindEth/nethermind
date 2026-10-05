// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using Ethereum.Test.Base;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;
using YamlDotNet.RepresentationModel;

namespace Nethermind.BeaconChain.Test.Types;

// Independent roots: ethereum/ssz-specs using spec-declared fields, not production encoders. Named-field/root cases come from original consensus-spec ssz_static fixtures. Body round-trip alone is not an independent oracle.
public class GloasContainerTests
{
    [Test]
    public void Builder_hash_tree_root_matches_an_independently_computed_value()
    {
        Builder builder = new()
        {
            Pubkey = Pubkey(0xA0),
            Version = 0,
            ExecutionAddress = new Address(Filled(Address.Size, 0xA1)),
            Balance = 32_000_000_000,
            DepositEpoch = 500_000,
            WithdrawableEpoch = ulong.MaxValue,
        };

        AssertRoundTripsAndMatchesRoot(builder, "0x85b4caa9007afd4605ad383f45294c3efb78ae4ce35348da60ead199ae166463");
    }

    [Test]
    public void BuilderPendingWithdrawal_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderPendingWithdrawal withdrawal = new()
        {
            FeeRecipient = new Address(Filled(Address.Size, 0xB0)),
            Amount = 1_000_000_000,
            BuilderIndex = 7,
        };

        AssertRoundTripsAndMatchesRoot(withdrawal, "0x807a2c0b918f9fdeaa0d8f84cc7b87aa441c8091c7b07abe50f81fc0a99bfbd6");
    }

    [Test]
    public void BuilderPendingPayment_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderPendingPayment payment = new()
        {
            Weight = 123_456,
            Withdrawal = new BuilderPendingWithdrawal
            {
                FeeRecipient = new Address(Filled(Address.Size, 0xB0)),
                Amount = 1_000_000_000,
                BuilderIndex = 7,
            },
            ProposerIndex = 42,
        };

        AssertRoundTripsAndMatchesRoot(payment, "0xfea1afeaba53f8ec3224a442a92ff2963fca55f27377fb0d3bfbc4d876c09469");
    }

    [Test]
    public void BuilderDepositRequest_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderDepositRequest request = new()
        {
            Pubkey = Pubkey(0xC0),
            WithdrawalCredentials = Hash(0xC1),
            Amount = 32_000_000_000,
            Signature = Signature(0xC2),
        };

        AssertRoundTripsAndMatchesRoot(request, "0x9583b5073b93f6c43168ee4d30fb805338b6616184f318d9b5acd291ccccb4e5");
    }

    [Test]
    public void BuilderExitRequest_hash_tree_root_matches_an_independently_computed_value()
    {
        BuilderExitRequest request = new()
        {
            SourceAddress = new Address(Filled(Address.Size, 0xD0)),
            Pubkey = Pubkey(0xD1),
        };

        AssertRoundTripsAndMatchesRoot(request, "0xff55245250859ea9c1cc7a15a196a9bc37b1ee348fb71b6ae1c51d40eaa1feb0");
    }

    [Test]
    public void PayloadAttestationData_hash_tree_root_matches_an_independently_computed_value()
    {
        PayloadAttestationData data = new()
        {
            BeaconBlockRoot = Hash(0xE0),
            Slot = 11_649_024,
            PayloadPresent = true,
            BlobDataAvailable = false,
        };

        AssertRoundTripsAndMatchesRoot(data, "0xfadac1fabeb7d8313dc7cd07feb19348bda5a2d662454db338c7043cdc4e5226");
    }

    [Test]
    public void PayloadAttestationMessage_hash_tree_root_matches_an_independently_computed_value()
    {
        PayloadAttestationMessage message = new()
        {
            ValidatorIndex = 17,
            Data = new PayloadAttestationData
            {
                BeaconBlockRoot = Hash(0xE0),
                Slot = 11_649_024,
                PayloadPresent = true,
                BlobDataAvailable = false,
            },
            Signature = Signature(0xE1),
        };

        AssertRoundTripsAndMatchesRoot(message, "0xdddbeec88a8d1479ff53e12e675553f0bedbef6a503f8d43e1ce51f810e74c96");
    }

    [Test]
    public void ExecutionPayloadBid_all_default_hash_tree_root_matches_an_independently_computed_value() =>
        AssertRoundTripsAndMatchesRoot(new ExecutionPayloadBid(), "0x83b932ee5875c06aa35328e3c3e3c976c703f2f4b1bc98e32991ceabbb2e4b63");

    [Test]
    public void ExecutionPayloadBid_with_values_round_trips_with_a_stable_root()
    {
        ExecutionPayloadBid bid = new()
        {
            ParentBlockHash = Hash(0x01),
            ParentBlockRoot = Hash(0x02),
            BlockHash = Hash(0x03),
            PrevRandao = Hash(0x04),
            FeeRecipient = new Address(Filled(Address.Size, 0x05)),
            GasLimit = 36_000_000,
            BuilderIndex = 3,
            Slot = 11_649_024,
            Value = 32_000_000_000,
            ExecutionPayment = 1_000_000_000,
            BlobKzgCommitments = [SszKzgCommitmentOf(0x06)],
            ExecutionRequestsRoot = Hash(0x07),
        };

        byte[] encoded = ExecutionPayloadBid.Encode(bid);
        ExecutionPayloadBid.Decode(encoded, out ExecutionPayloadBid decoded);
        Assert.That(ExecutionPayloadBid.Encode(decoded), Is.EqualTo(encoded));
        ExecutionPayloadBid.Merkleize(bid, out UInt256 root);
        ExecutionPayloadBid.Merkleize(decoded, out UInt256 decodedRoot);
        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(decodedRoot, Is.EqualTo(root));
        Assert.That(root, Is.Not.EqualTo(UInt256.Zero));
        Assert.That(decoded.BlobKzgCommitments, Has.Length.EqualTo(1));
    }

    [Test]
    public void BeaconBlockBodyGloas_round_trips_with_a_stable_root()
    {
        BeaconBlockBodyGloas body = new()
        {
            RandaoReveal = Signature(0x40),
            Eth1Data = new Eth1Data { DepositRoot = Hash(0x41), DepositCount = 1, BlockHash = Hash(0x42) },
            Graffiti = Hash(0x43),
            ProposerSlashings = [],
            AttesterSlashings = [],
            Attestations = [],
            Deposits = [],
            VoluntaryExits = [],
            SyncAggregate = new SyncAggregate { SyncCommitteeBits = new BitArray(512), SyncCommitteeSignature = Signature(0x44) },
            BlsToExecutionChanges = [],
            SignedExecutionPayloadBid = new SignedExecutionPayloadBid
            {
                Message = new ExecutionPayloadBid
                {
                    ParentBlockHash = Hash(0x45),
                    ParentBlockRoot = Hash(0x46),
                    BlockHash = Hash(0x47),
                    PrevRandao = Hash(0x48),
                    FeeRecipient = new Address(Filled(Address.Size, 0x49)),
                    GasLimit = 36_000_000,
                    BuilderIndex = ulong.MaxValue,
                    Slot = 123,
                    Value = 0,
                    ExecutionPayment = 0,
                    BlobKzgCommitments = [],
                    ExecutionRequestsRoot = Hash(0x4A),
                },
                Signature = Signature(0x4B),
            },
            PayloadAttestations = [],
            ParentExecutionRequests = new ExecutionRequestsGloas(),
        };

        AssertRoundTrips(body, BeaconBlockBodyGloas.Encode, BeaconBlockBodyGloas.Decode, BeaconBlockBodyGloas.Merkleize);
    }

    [Test]
    public void BeaconStateGloas_all_default_hash_tree_root_matches_an_independently_computed_value()
    {
        BeaconStateGloas state = new()
        {
            Fork = new Fork(),
            LatestBlockHeader = new BeaconBlockHeader(),
            Eth1Data = new Eth1Data(),
            PreviousJustifiedCheckpoint = new Checkpoint(),
            CurrentJustifiedCheckpoint = new Checkpoint(),
            FinalizedCheckpoint = new Checkpoint(),
            CurrentSyncCommittee = new SyncCommittee(),
            NextSyncCommittee = new SyncCommittee(),
            LatestBlockHash = Hash256.Zero,
            ExecutionPayloadAvailability = new BitArray(8192),
            BuilderPendingPayments = [.. Enumerable.Repeat(0, 64).Select(static _ => new BuilderPendingPayment { Withdrawal = new BuilderPendingWithdrawal() })],
            LatestExecutionPayloadBid = new ExecutionPayloadBid(),
            PtcWindow = [.. Enumerable.Repeat(0, 96).Select(static _ => new PayloadTimelinessCommittee())],
        };

        AssertRoundTripsAndMatchesRoot(state, "0x1971a1bc7e155511766c64b6a2121317d01fa040ffa6da5f93c3629f60fe3166");
    }

    [TestCaseSource(nameof(NamedFixtureCases))]
    public void Named_fields_and_root_match_the_consensus_spec_fixture(Action check) => check();

    private static IEnumerable<TestCaseData> NamedFixtureCases()
    {
        yield return new TestCaseData((Action)(() => AssertNamedFixture<SignedExecutionPayloadBid>("case_3", "0x0e731d14cea238f3dca8c9b1da6a20a6fcef9b4c37a5a076197e4425d9b8c3e3")))
            .SetName("SignedExecutionPayloadBid_hash_tree_root_matches_the_consensus_spec_fixture");
        yield return new TestCaseData((Action)(() => AssertNamedFixture<ExecutionPayloadEnvelope>("case_4", "0xf1e3a366656a523322d0e74a170ed13fe971925a59825c7dfadf70116087843e")))
            .SetName("ExecutionPayloadEnvelope_hash_tree_root_matches_the_consensus_spec_fixture");
        yield return new TestCaseData((Action)(() => AssertNamedFixture<SignedExecutionPayloadEnvelope>("case_3", "0x73440a71df59ff10cd1c3f9f26d89408471cf8813e01ca946406268cf53e9805")))
            .SetName("SignedExecutionPayloadEnvelope_hash_tree_root_matches_the_consensus_spec_fixture");
        yield return new TestCaseData((Action)(() => AssertNamedFixture<PayloadAttestation>("case_2", "0xe77c4e19bf83c95dabd72815a4e56b150f177fac2349caa7356416c3989e26ce")))
            .SetName("PayloadAttestation_hash_tree_root_matches_the_consensus_spec_fixture");
        yield return new TestCaseData((Action)(() => AssertNamedFixture<IndexedPayloadAttestation>("case_4", "0x12cc133cdb17b32ac8a9c0a0faaa06b2a49d685ba98c8b929350c3bc7c657f9e")))
            .SetName("IndexedPayloadAttestation_hash_tree_root_matches_the_consensus_spec_fixture");
    }

    private static void AssertNamedFixture<T>(string caseName, string expectedRoot) where T : class, ISszCodec<T>
    {
        // Later releases changed these random inputs; retain the original named-field and root oracles.
        string root = TestFixtureDownloader.EnsureDownloaded("GloasFiveMainnet", "https://github.com/ethereum/consensus-specs/releases/download/{0}/{1}",
            "v1.7.0-alpha.13", "mainnet.tar.gz", static path => path.Contains("/gloas/ssz_static/") &&
                (path.Contains("/SignedExecutionPayloadBid/ssz_random/case_3/") || path.Contains("/ExecutionPayloadEnvelope/ssz_random/case_4/") ||
                 path.Contains("/SignedExecutionPayloadEnvelope/ssz_random/case_3/") || path.Contains("/PayloadAttestation/ssz_random/case_2/") ||
                 path.Contains("/IndexedPayloadAttestation/ssz_random/case_4/")), "gloas-five-alpha13");
        string path = Path.Combine(root, "tests", "mainnet", "gloas", "ssz_static", typeof(T).Name, "ssz_random", caseName);
        using StreamReader reader = new(Path.Combine(path, "value.yaml"));
        YamlStream yaml = [];
        yaml.Load(reader);
        byte[] canonical = Snappier.Snappy.DecompressToArray(File.ReadAllBytes(Path.Combine(path, "serialized.ssz_snappy")));
        T.Decode(canonical, out T decoded);
        AssertNamedValue(decoded, yaml.Documents[0].RootNode, typeof(T).Name);
        Assert.That(T.Encode(decoded), Is.EqualTo(canonical));
        AssertRoundTripsAndMatchesRoot(decoded, expectedRoot);
    }

    private static void AssertNamedValue(object? actual, YamlNode expected, string path)
    {
        Assert.That(actual, Is.Not.Null, path);
        if (expected is YamlMappingNode mapping)
        {
            foreach (KeyValuePair<YamlNode, YamlNode> field in mapping.Children)
            {
                string name = ((YamlScalarNode)field.Key).Value!;
                PropertyInfo property = actual!.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Single(p => string.Equals(p.Name, name.Replace("_", ""), StringComparison.OrdinalIgnoreCase));
                AssertNamedValue(property.GetValue(actual), field.Value, $"{path}.{name}");
            }
        }
        else if (expected is YamlSequenceNode sequence)
        {
            Array items = (Array)actual!;
            Assert.That(items.Length, Is.EqualTo(sequence.Children.Count), $"{path}.length");
            for (int i = 0; i < items.Length; i++)
                AssertNamedValue(items.GetValue(i), sequence.Children[i], $"{path}[{i}]");
        }
        else
        {
            string value = ((YamlScalarNode)expected).Value!;
            string actualText = actual switch
            {
                byte[] bytes => "0x" + Convert.ToHexString(bytes),
                TransactionGloas transaction => "0x" + Convert.ToHexString(transaction.Bytes!),
                SszKzgCommitment commitment => "0x" + Convert.ToHexString(commitment.AsSpan()),
                BitArray bits => BitText(bits),
                bool flag => flag ? "true" : "false",
                Bloom bloom => "0x" + Convert.ToHexString(bloom.ReadOnlyBytes),
                Hash256 or Address or BlsPublicKey or BlsSignature => actual.ToString()!,
                ulong number => number.ToString(CultureInfo.InvariantCulture),
                UInt256 number => number.ToString(),
                _ => throw new InvalidDataException($"{path}: unsupported named value {actual!.GetType().Name}"),
            };
            Assert.That(actualText, Is.EqualTo(value).IgnoreCase, path);
        }
    }

    private static string BitText(BitArray bits)
    {
        byte[] bytes = new byte[(bits.Length + 7) / 8];
        bits.CopyTo(bytes, 0);
        return "0x" + Convert.ToHexString(bytes);
    }

    private static void AssertRoundTripsAndMatchesRoot<T>(T value, string expectedRootHex) where T : class, ISszCodec<T>
    {
        AssertRoundTrips(value, T.Encode, T.Decode, T.Merkleize);
        Hash256 root = SszRoots.HashTreeRoot(value);
        Assert.That(root.ToString(), Is.EqualTo(expectedRootHex).IgnoreCase);
    }

    private static void AssertRoundTrips<T>(T value, System.Func<T, byte[]> encode, DecodeDelegate<T> decode, MerkleizeDelegate<T> merkleize)
    {
        byte[] encoded = encode(value);
        decode(encoded, out T decoded);
        byte[] reEncoded = encode(decoded);
        merkleize(value, out UInt256 originalRoot);
        merkleize(decoded, out UInt256 decodedRoot);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(reEncoded, Is.EqualTo(encoded));
        Assert.That(decodedRoot, Is.EqualTo(originalRoot));
    }

    private delegate void DecodeDelegate<T>(ReadOnlySpan<byte> data, out T value);
    private delegate void MerkleizeDelegate<T>(T value, out UInt256 root);

    private static byte[] Filled(int length, byte value) => Enumerable.Repeat(value, length).ToArray();

    private static Hash256 Hash(byte value) => new(Filled(Hash256.Size, value));

    private static byte[] Hex(string hex) => Bytes.FromHexString(hex);

    private static Nethermind.Merge.Plugin.SszRest.SszKzgCommitment Kzg(string hex) =>
        Nethermind.Merge.Plugin.SszRest.SszKzgCommitment.FromSpan(Hex(hex));

    private static BlsPublicKey Pubkey(byte value) => new(Filled(BlsPublicKey.Length, value));

    private static BlsSignature Signature(byte value) => new(Filled(BlsSignature.Length, value));

    private static Nethermind.Merge.Plugin.SszRest.SszKzgCommitment SszKzgCommitmentOf(byte value) =>
        Nethermind.Merge.Plugin.SszRest.SszKzgCommitment.FromSpan(Filled(Nethermind.Merge.Plugin.SszRest.SszKzgCommitment.KzgCommitmentLength, value));
}
