// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

extern alias stateless;

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Ethereum.Test.Base;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Serialization.Ssz;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.GnosisForks;
using Nethermind.Stateless.Execution;
using Nethermind.Stateless.Execution.IO;
using NUnit.Framework;
using StatelessExecutionPayloadV1 = stateless::Nethermind.Merge.Plugin.SszRest.SszExecutionPayloadV1;
using StatelessExecutionPayloadV3 = stateless::Nethermind.Merge.Plugin.SszRest.SszExecutionPayloadV3;
using StatelessExecutionPayloadV4 = stateless::Nethermind.Merge.Plugin.SszRest.SszExecutionPayloadV4;

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
    [TestCase(ProtocolFork.Cancun)]
    [TestCase(ProtocolFork.Prague)]
    [TestCase(ProtocolFork.Osaka)]
    [TestCase(ProtocolFork.BPO1)]
    [TestCase(ProtocolFork.BPO2)]
    [TestCase(ProtocolFork.Amsterdam)]
    public void Revision_1_schema_roundtrips(ProtocolFork fork)
    {
        byte[] encoded = fork == ProtocolFork.Amsterdam
            ? EncodeInput<StatelessExecutionPayloadV4>(fork)
            : EncodeInput<StatelessExecutionPayloadV3>(fork);

        StatelessPayload payload = InputDecoder.Decode(encoded);
        bool foundByName = ProtocolForkExtensions.TryGetByName(fork.GetName(), out ProtocolFork forkByName);
        Hash256 expectedRequestsHash = fork >= ProtocolFork.Prague ? ExecutionRequestExtensions.EmptyRequestsHash : null;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.ProtocolFork, Is.EqualTo(fork));
            Assert.That(payload.ChainConfig.ChainId, Is.EqualTo(1));
            Assert.That(foundByName, Is.True);
            Assert.That(forkByName, Is.EqualTo(fork));
            Assert.That(payload.Block.Header.RequestsHash, Is.EqualTo(expectedRequestsHash));
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

    [TestCase(0x0f01)]
    [TestCase(0x1002)]
    [TestCase(0x1601)]
    public void Unsupported_schema_id_is_rejected(int schemaId)
    {
        byte[] encoded = new byte[sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(encoded, (ushort)schemaId);

        Assert.That(
            () => InputDecoder.Decode(encoded),
            Throws.TypeOf<ArgumentException>().With.Message.Contains($"0x{schemaId:x4}"));
    }

    [TestCase(10UL, 20UL, true)]
    [TestCase(9UL, 20UL, false)]
    [TestCase(10UL, 19UL, false)]
    public void Every_fork_activation_bound_must_be_active(ulong blockNumber, ulong timestamp, bool expected)
    {
        SszForkActivation activation = new() { BlockNumber = [10], Timestamp = [20] };

        Assert.That(activation.IsActive(CreateHeader(blockNumber, timestamp)), Is.EqualTo(expected));
    }

    [Test]
    public void Fork_activation_requires_at_least_one_bound()
    {
        SszForkActivation activation = new() { BlockNumber = [], Timestamp = [] };

        Assert.That(() => activation.IsActive(CreateHeader(10, 20)), Throws.TypeOf<InvalidDataException>());
    }

    [TestCase(BlockchainIds.Sepolia, false)]
    [TestCase(BlockchainIds.Gnosis, true)]
    [TestCase(BlockchainIds.Chiado, true)]
    public void Amsterdam_schema_uses_chain_appropriate_fork_catalog(ulong chainId, bool usesGnosisRules)
    {
        IForkAwareSpecProvider baseProvider = chainId switch
        {
            BlockchainIds.Sepolia => SepoliaSpecProvider.Instance,
            BlockchainIds.Gnosis => GnosisSpecProvider.Instance,
            BlockchainIds.Chiado => ChiadoSpecProvider.Instance,
            _ => throw new AssertionException($"Unsupported test chain: {chainId}")
        };
        ForkConfig forkConfig = new()
        {
            Activation = new SszForkActivation { BlockNumber = [], Timestamp = [20] }
        };

        ISpecProvider provider = StatelessSpecProvider.Create(baseProvider, chainId, forkConfig, ProtocolFork.Amsterdam);
        IReleaseSpec spec = provider.GetSpec(new ForkActivation(1, 20));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provider.ChainId, Is.EqualTo(chainId));
            Assert.That(spec.Name, Is.EqualTo(Amsterdam.Instance.Name));
            Assert.That(spec, usesGnosisRules ? Is.SameAs(AmsterdamGnosis.Instance) : Is.SameAs(Amsterdam.Instance));
        }
    }

    private static byte[] EncodeInput<TExecutionPayload>(ProtocolFork fork)
        where TExecutionPayload : StatelessExecutionPayloadV1,
        stateless::Nethermind.Merge.Plugin.SszRest.ISszExecutionPayloadFactory<TExecutionPayload>,
        ISszCodec<TExecutionPayload>, new()
    {
        StatelessInput<TExecutionPayload> input = new()
        {
            NewPayloadRequest = new()
            {
                ExecutionPayload = new TExecutionPayload(),
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
            ChainConfig = new()
            {
                ChainId = 1,
                ActiveFork = new()
                {
                    Activation = new()
                    {
                        BlockNumber = [0],
                        Timestamp = []
                    }
                }
            },
            PublicKeys = []
        };
        byte[] payload = StatelessInput<TExecutionPayload>.Encode(input);
        byte[] encoded = new byte[sizeof(ushort) + payload.Length];

        BinaryPrimitives.WriteUInt16BigEndian(encoded, fork.ToRevision1SchemaId());
        payload.AsSpan().CopyTo(encoded.AsSpan(sizeof(ushort)));

        return encoded;
    }

    private static BlockHeader CreateHeader(ulong blockNumber, ulong timestamp) => new(
        Hash256.Zero,
        Hash256.Zero,
        Address.Zero,
        UInt256.Zero,
        blockNumber,
        0,
        timestamp,
        []);
}
