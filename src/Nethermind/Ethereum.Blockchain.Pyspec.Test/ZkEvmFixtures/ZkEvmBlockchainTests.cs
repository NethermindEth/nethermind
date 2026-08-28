// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Serialization.Ssz;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;
using Nethermind.Stateless.Execution;
using Nethermind.Stateless.Execution.IO;
using NUnit.Framework;

namespace Ethereum.Blockchain.Pyspec.Test.ZkEvmFixtures;

public class ZkEvmBlockchainTests : ZkEvmBlockchainTestFixture;

public abstract class ZkEvmBlockchainTestFixture : PyspecLinuxX64BlockchainFixture
{
    protected ZkEvmBlockchainTestFixture() : base(parallel: false, batchRead: false) { }

    private static readonly Lazy<IReadOnlyList<BlockchainTest>> _tests = new(() =>
        ZkEvmMutatedWitnessIndex.StampMutatedBlocks(
            new TestsSourceLoader(
                new LoadPyspecTestsStrategy { ArchiveVersion = Constants.ArchiveVersion, ArchiveName = Constants.ArchiveName },
                "fixtures/blockchain_tests").LoadTests<BlockchainTest>()).ToList());

    [TestCaseSource(nameof(LoadWitnessTests))]
    public async Task WitnessMatchesFixture(BlockchainTest test) => Assert.That((await RunTest(test)).Pass, Is.True);

    [TestCaseSource(nameof(LoadStatelessTests))]
    public void StatelessExecutorOutputMatchesFixture(string inputBytes, string expectedOutputBytes)
    {
        if (!inputBytes.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"StatelessInputBytes must be 0x-prefixed.");

        if (!expectedOutputBytes.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"StatelessOutputBytes must be 0x-prefixed.");

        byte[] actualOutput = StatelessExecutor.Execute(Convert.FromHexString(inputBytes[2..]));
        byte[] expectedOutput = Convert.FromHexString(expectedOutputBytes[2..]);

        Assert.That(actualOutput, Is.EqualTo(expectedOutput),
            $"Expected {expectedOutput.ToHexString(true)}, got {actualOutput.ToHexString(true)}");
    }

    private static IEnumerable<TestCaseData> LoadWitnessTests() => PyspecLoader.ToTestCases(_tests.Value);

    private static IEnumerable<TestCaseData> LoadStatelessTests()
    {
        foreach (BlockchainTest test in _tests.Value)
        {
            if (test.Blocks is not { Length: > 0 } blocks)
                continue;

            for (int i = 0; i < blocks.Length; i++)
            {
                TestBlockJson block = blocks[i];

                if (block.StatelessInputBytes is null && block.StatelessOutputBytes is null)
                    continue;

                if (block.StatelessInputBytes is null || block.StatelessOutputBytes is null)
                    throw new InvalidDataException($"Incomplete stateless fixture data in {test.Name}, block {i}.");

                yield return new TestCaseData(block.StatelessInputBytes, block.StatelessOutputBytes)
                    .SetName($"{test.Name}_stateless_block_{i}");
            }
        }
    }
}

[TestFixture]
public class StatelessSchemaTests
{
    private const ulong ChainId = BlockchainIds.Mainnet;

    // Past every Mainnet fork activation, so the current-fork schema resolves to the newest known rules
    private const ulong BlockNumber = 30_000_000;
    private const ulong Timestamp = 2_000_000_000;

    // Wire bytes of a current-fork input carrying DeterministicPublicKeys(5); see Public_key_vector_encoding_is_pinned.
    private const int PinnedPublicKeyInputLength = 951;
    private const string PinnedPublicKeyInputHash = "926d9f2ddf31daf7182e5bea35a3732a8b8d232682e950ce748ea39f8a15bb86";

    [TestCase(InputDecoder.CurrentForkSchemaId)]
    [TestCase(InputDecoder.AmsterdamSchemaId)]
    public void Revision_1_schema_roundtrips(ushort schemaId)
    {
        byte[] encoded = schemaId == InputDecoder.AmsterdamSchemaId
            ? EncodeInput(new SszExecutionPayloadAmsterdam(), schemaId)
            : EncodeInput(new SszExecutionPayload(), schemaId);

        StatelessPayload payload = InputDecoder.Decode(encoded);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.SchemaId, Is.EqualTo(schemaId));
            Assert.That(payload.ChainId, Is.EqualTo(ChainId));
            Assert.That(payload.GetBlock().Header.RequestsHash, Is.EqualTo(ExecutionRequestExtensions.EmptyRequestsHash));
        }
    }

    /// <summary>
    /// Pins the wire bytes a non-empty public-key list produces, and that they decode back unchanged.
    /// </summary>
    /// <remarks>
    /// The hash is the guard that matters: a round-trip alone passes when
    /// <see cref="SszPublicKeyVectorTypeConverter"/>'s write and read sides are perturbed together.
    /// </remarks>
    [Test]
    public void Public_key_vector_encoding_is_pinned()
    {
        SszPublicKey[] publicKeys = DeterministicPublicKeys(5);

        byte[] encoded = EncodeInput(new SszExecutionPayload(), InputDecoder.CurrentForkSchemaId, publicKeys: publicKeys);
        ReadOnlySpan<SszPublicKey> decoded = InputDecoder.Decode(encoded).PublicKeys.Span;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(encoded, Has.Length.EqualTo(PinnedPublicKeyInputLength));
            Assert.That(SHA256.HashData(encoded).ToHexString(), Is.EqualTo(PinnedPublicKeyInputHash));
            Assert.That(decoded.Length, Is.EqualTo(publicKeys.Length));
        }

        for (int i = 0; i < publicKeys.Length; i++)
            Assert.That(decoded[i].AsSpan().ToArray(), Is.EqualTo(publicKeys[i].AsSpan().ToArray()));
    }

    private static SszPublicKey[] DeterministicPublicKeys(int count)
    {
        SszPublicKey[] publicKeys = new SszPublicKey[count];

        for (int i = 0; i < count; i++)
        {
            byte[] bytes = new byte[SszPublicKey.PublicKeyLength];
            bytes[0] = 0x04;

            for (int j = 1; j < bytes.Length; j++)
                bytes[j] = (byte)(i * 31 + j);

            publicKeys[i] = SszPublicKey.FromSpan(bytes);
        }

        return publicKeys;
    }

    /// <summary>
    /// Block reconstruction must stay out of <see cref="InputDecoder.Decode"/>: a throw before
    /// <see cref="StatelessExecutor.FailureOutput"/> is published reports the zero sentinel.
    /// </summary>
    [Test]
    public void Decoding_defers_block_reconstruction()
    {
        byte[] encoded = EncodeInput(new SszExecutionPayload(), InputDecoder.CurrentForkSchemaId, MalformedTransaction);

        StatelessPayload payload = InputDecoder.Decode(encoded);

        Assert.That(payload.GetBlock, Throws.InvalidOperationException);
    }

    [Test]
    public void Malformed_transaction_rlp_reports_the_decoded_metadata()
    {
        byte[] encoded = EncodeInput(new SszExecutionPayload(), InputDecoder.CurrentForkSchemaId, MalformedTransaction);
        byte[] expected = StatelessValidationResult.Encode(new StatelessValidationResult
        {
            NewPayloadRequestRoot = InputDecoder.Decode(encoded).NewPayloadRequestRoot,
            IsSuccess = false,
            ChainId = ChainId,
            SchemaId = InputDecoder.CurrentForkSchemaId
        });

        Assert.That(StatelessExecutor.Execute(encoded), Is.EqualTo(expected));
    }

    [TestCase(ProtocolFork.Cancun)]
    [TestCase(ProtocolFork.Prague)]
    [TestCase(ProtocolFork.Osaka)]
    [TestCase(ProtocolFork.BPO1)]
    [TestCase(ProtocolFork.BPO2)]
    [TestCase(ProtocolFork.Amsterdam)]
    public void Fork_name_roundtrips(ProtocolFork fork)
    {
        bool foundByName = ProtocolForkExtensions.TryGetByName(fork.GetName(), out ProtocolFork forkByName);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(foundByName, Is.True);
            Assert.That(forkByName, Is.EqualTo(fork));
        }
    }

    [TestCase((byte)ExecutionRequestType.Deposit, ExecutionRequestExtensions.DepositRequestsBytesSize)]
    [TestCase((byte)ExecutionRequestType.WithdrawalRequest, ExecutionRequestExtensions.WithdrawalRequestsBytesSize)]
    [TestCase((byte)ExecutionRequestType.ConsolidationRequest, ExecutionRequestExtensions.ConsolidationRequestsBytesSize)]
    [TestCase((byte)ExecutionRequestType.BuilderDepositRequest, ExecutionRequestExtensions.BuilderDepositRequestsBytesSize)]
    [TestCase((byte)ExecutionRequestType.BuilderExitRequest, ExecutionRequestExtensions.BuilderExitRequestsBytesSize)]
    public void Request_struct_conversion_roundtrips(byte requestType, int size)
    {
        byte[] data = new byte[size];

        for (int i = 0; i < data.Length; i++)
            data[i] = (byte)i;

        ExecutionRequest request = new() { RequestType = requestType, RequestData = data };
        ExecutionRequest roundTripped = (ExecutionRequestType)requestType switch
        {
            ExecutionRequestType.Deposit => DepositRequest.From(request).ToExecutionRequest(),
            ExecutionRequestType.WithdrawalRequest => WithdrawalRequest.From(request).ToExecutionRequest(),
            ExecutionRequestType.ConsolidationRequest => ConsolidationRequest.From(request).ToExecutionRequest(),
            ExecutionRequestType.BuilderDepositRequest => BuilderDepositRequest.From(request).ToExecutionRequest(),
            ExecutionRequestType.BuilderExitRequest => BuilderExitRequest.From(request).ToExecutionRequest(),
            _ => throw new AssertionException($"Unsupported request type: {requestType}")
        };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(roundTripped.RequestType, Is.EqualTo(requestType));
            Assert.That(roundTripped.RequestData, Is.EqualTo(data));
        }
    }

    [TestCase(0)]
    [TestCase(1)]
    public void Schema_prefix_must_be_two_bytes(int length)
    {
        byte[] encoded = new byte[length];

        Assert.That(() => InputDecoder.Decode(encoded), Throws.TypeOf<ArgumentOutOfRangeException>());
    }

    [TestCase(0x0000)]
    [TestCase(0x0002)]
    [TestCase(0x1001)]
    [TestCase(0x1401)]
    [TestCase(0x1502)]
    [TestCase(0x1601)]
    public void Unsupported_schema_id_is_rejected(int schemaId)
    {
        byte[] encoded = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(encoded, (ushort)schemaId);

        Assert.That(
            () => InputDecoder.Decode(encoded),
            Throws.TypeOf<ArgumentException>().With.Message.Contains($"0x{schemaId:x4}"));
    }

    [TestCase(BlockchainIds.Sepolia, false)]
    [TestCase(BlockchainIds.Gnosis, true)]
    [TestCase(BlockchainIds.Chiado, true)]
    public void Amsterdam_schema_uses_chain_appropriate_fork_catalog(ulong chainId, bool usesGnosisRules)
    {
        ForkActivation activation = new(1, 20);
        ISpecProvider provider = StatelessSpecProvider.Create(chainId, ProtocolFork.Amsterdam, activation);
        IReleaseSpec spec = provider.GetSpec(activation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.ChainId, Is.EqualTo(chainId));
            Assert.That(spec.Name, Is.EqualTo(Amsterdam.Instance.Name));
            Assert.That(spec, usesGnosisRules ? Is.SameAs(AmsterdamGnosis.Instance) : Is.SameAs(Amsterdam.Instance));
        }
    }

    [TestCase(BlockchainIds.Mainnet)]
    [TestCase(BlockchainIds.Sepolia)]
    [TestCase(BlockchainIds.Gnosis)]
    public void Current_fork_schema_takes_the_rules_from_the_chain_schedule(ulong chainId)
    {
        IForkAwareSpecProvider baseProvider = chainId switch
        {
            BlockchainIds.Mainnet => MainnetSpecProvider.Instance,
            BlockchainIds.Sepolia => SepoliaSpecProvider.Instance,
            BlockchainIds.Gnosis => GnosisSpecProvider.Instance,
            _ => throw new AssertionException($"Unsupported test chain: {chainId}")
        };
        ForkActivation activation = new(BlockNumber, Timestamp);
        ISpecProvider provider = StatelessSpecProvider.Create(chainId, ProtocolFork.Current, activation);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.ChainId, Is.EqualTo(chainId));
            Assert.That(provider.GetSpec(activation).Name, Is.EqualTo(baseProvider.GetSpec(activation).Name));
        }
    }

    [TestCaseSource(nameof(BlobVersionedHashCases))]
    public bool Blob_versioned_hashes_must_match_in_payload_order(Transaction[] transactions, Hash256[] expected) =>
        StatelessExecutor.BlobVersionedHashesMatch(transactions, expected);

    private static IEnumerable<TestCaseData> BlobVersionedHashCases()
    {
        yield return new TestCaseData(new[] { BlobTx(1, 2), BlobTx(3) }, Hashes(1, 2, 3))
            .Returns(true).SetName("Matching hashes in payload order");
        yield return new TestCaseData(new[] { new Transaction(), BlobTx(1), new Transaction() }, Hashes(1))
            .Returns(true).SetName("Non-blob transactions are skipped");
        yield return new TestCaseData(new[] { new Transaction() }, Hashes())
            .Returns(true).SetName("No blob transactions and no hashes");
        yield return new TestCaseData(new[] { BlobTx(1, 2), BlobTx(3) }, Hashes(1, 3, 2))
            .Returns(false).SetName("Hashes out of payload order");
        yield return new TestCaseData(new[] { BlobTx(1, 2) }, Hashes(1))
            .Returns(false).SetName("Fewer hashes than the payload commits to");
        yield return new TestCaseData(new[] { BlobTx(1) }, Hashes(1, 2))
            .Returns(false).SetName("More hashes than the payload commits to");
    }

    private static Transaction BlobTx(params byte[] ids)
    {
        byte[][] hashes = new byte[ids.Length][];

        for (int i = 0; i < ids.Length; i++)
            hashes[i] = HashBytes(ids[i]);

        return new Transaction { BlobVersionedHashes = hashes };
    }

    private static Hash256[] Hashes(params byte[] ids)
    {
        Hash256[] hashes = new Hash256[ids.Length];

        for (int i = 0; i < ids.Length; i++)
            hashes[i] = new Hash256(HashBytes(ids[i]));

        return hashes;
    }

    private static byte[] HashBytes(byte id)
    {
        byte[] bytes = new byte[Hash256.Size];

        bytes[^1] = id;

        return bytes;
    }

    /// <summary>Transaction payload that is well-formed SSZ but not decodable as transaction RLP.</summary>
    private static SszProgressiveBytes[] MalformedTransaction => [new() { Bytes = [0xff, 0xff] }];

    private static byte[] EncodeInput<TExecutionPayload>(
        TExecutionPayload executionPayload, ushort schemaId, SszProgressiveBytes[] transactions = null,
        SszPublicKey[] publicKeys = null)
        where TExecutionPayload : SszExecutionPayload, ISszCodec<TExecutionPayload>, new()
    {
        executionPayload.BlockNumber = BlockNumber;
        executionPayload.Timestamp = Timestamp;

        if (transactions is not null)
            executionPayload.Transactions = transactions;

        StatelessInput<TExecutionPayload> input = new()
        {
            NewPayloadRequest = new()
            {
                ExecutionPayload = executionPayload,
                VersionedHashes = [],
                ParentBeaconBlockRoot = Hash256.Zero,
                ExecutionRequests = new()
                {
                    Deposits = [],
                    Withdrawals = [],
                    Consolidations = [],
                    BuilderDeposits = [],
                    BuilderExits = []
                }
            },
            Witness = new()
            {
                State = [],
                Codes = [],
                Headers = []
            },
            ChainId = ChainId,
            PublicKeys = publicKeys ?? []
        };
        byte[] payload = StatelessInput<TExecutionPayload>.Encode(input);
        byte[] encoded = new byte[sizeof(ushort) + payload.Length];

        BinaryPrimitives.WriteUInt16BigEndian(encoded, schemaId);
        payload.AsSpan().CopyTo(encoded.AsSpan(sizeof(ushort)));

        return encoded;
    }
}
