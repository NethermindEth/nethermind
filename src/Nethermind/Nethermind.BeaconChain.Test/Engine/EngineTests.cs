// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Autofac;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using NSubstitute;
using NUnit.Framework;
using BeaconExecutionPayload = Nethermind.BeaconChain.Types.ExecutionPayload;
using BeaconTransaction = Nethermind.BeaconChain.Types.Transaction;
using BeaconWithdrawal = Nethermind.BeaconChain.Types.Withdrawal;

namespace Nethermind.BeaconChain.Test.Engine;

public class EngineTests
{
    private static readonly Hash256 TestHash = new("0x1111111111111111111111111111111111111111111111111111111111111111");

    [Test]
    public void Payload_converter_maps_fields_versioned_hashes_and_requests()
    {
        byte[] commitment = Enumerable.Repeat((byte)0xaa, 48).ToArray();
        DepositRequest deposit = new()
        {
            Pubkey = new BlsPublicKey(Enumerable.Repeat((byte)0x01, 48).ToArray()),
            WithdrawalCredentials = TestHash,
            Amount = 32_000_000_000,
            Signature = new BlsSignature(Enumerable.Repeat((byte)0x02, 96).ToArray()),
            Index = 7,
        };
        ConsolidationRequest consolidation = new()
        {
            SourceAddress = Address.SystemUser,
            SourcePubkey = new BlsPublicKey(Enumerable.Repeat((byte)0x03, 48).ToArray()),
            TargetPubkey = new BlsPublicKey(Enumerable.Repeat((byte)0x04, 48).ToArray()),
        };

        BeaconExecutionPayload payload = new()
        {
            ParentHash = TestHash,
            FeeRecipient = Address.SystemUser,
            StateRoot = TestHash,
            ReceiptsRoot = TestHash,
            LogsBloom = Bloom.Empty,
            PrevRandao = TestHash,
            BlockNumber = 123,
            GasLimit = 30_000_000,
            GasUsed = 21_000,
            Timestamp = 1_700_000_000,
            ExtraData = Bytes.FromHexString("0xdeadbeef"),
            BaseFeePerGas = 7,
            BlockHash = TestHash,
            Transactions = [new BeaconTransaction { Bytes = Bytes.FromHexString("0x02abcd") }],
            Withdrawals = [new BeaconWithdrawal { Index = 1, ValidatorIndex = 2, Address = Address.SystemUser, Amount = 3 }],
            BlobGasUsed = 131072,
            ExcessBlobGas = 262144,
        };

        ExecutionPayloadV3 converted = PayloadConverter.ToExecutionPayloadV3(payload);
        Hash256?[] versionedHashes = PayloadConverter.ToBlobVersionedHashes([SszKzgCommitment.FromSpan(commitment)]);
        byte[][] requests = PayloadConverter.ToExecutionRequestsList(new ExecutionRequests
        {
            Deposits = [deposit],
            Withdrawals = [],
            Consolidations = [consolidation],
        });

        // EIP-4844 kzg_to_versioned_hash computed independently of the production helper
        byte[] expectedVersionedHash = SHA256.HashData(commitment);
        expectedVersionedHash[0] = 0x01;

        // EIP-7685: request_type || concatenated fixed-size SSZ requests, empty lists omitted
        byte[] expectedDeposits = new byte[1 + 192];
        deposit.Pubkey.Bytes.CopyTo(expectedDeposits.AsSpan(1));
        TestHash.Bytes.CopyTo(expectedDeposits.AsSpan(1 + 48));
        BinaryPrimitives.WriteUInt64LittleEndian(expectedDeposits.AsSpan(1 + 80), deposit.Amount);
        deposit.Signature.Bytes.CopyTo(expectedDeposits.AsSpan(1 + 88));
        BinaryPrimitives.WriteUInt64LittleEndian(expectedDeposits.AsSpan(1 + 184), deposit.Index);

        byte[] expectedConsolidations = new byte[1 + 116];
        expectedConsolidations[0] = 0x02;
        Address.SystemUser.Bytes.CopyTo(expectedConsolidations.AsSpan(1));
        consolidation.SourcePubkey.Bytes.CopyTo(expectedConsolidations.AsSpan(1 + 20));
        consolidation.TargetPubkey.Bytes.CopyTo(expectedConsolidations.AsSpan(1 + 68));

        Assert.Multiple(() =>
        {
            Assert.That(converted.BlockNumber, Is.EqualTo(123));
            Assert.That(converted.GasLimit, Is.EqualTo(30_000_000));
            Assert.That(converted.Timestamp, Is.EqualTo(1_700_000_000));
            Assert.That(converted.BlockHash, Is.EqualTo(TestHash));
            Assert.That(converted.Transactions, Is.EqualTo(new[] { Bytes.FromHexString("0x02abcd") }));
            Assert.That(converted.Withdrawals![0].AmountInGwei, Is.EqualTo(3ul));
            Assert.That(converted.BlobGasUsed, Is.EqualTo(131072ul));
            Assert.That(versionedHashes.Single()!.Bytes.ToArray(), Is.EqualTo(expectedVersionedHash));
            Assert.That(requests, Has.Length.EqualTo(2));
            Assert.That(requests[0], Is.EqualTo(expectedDeposits));
            Assert.That(requests[1], Is.EqualTo(expectedConsolidations));
        });
    }

    [TestCase(PayloadStatus.Valid, true)]
    [TestCase(PayloadStatus.Syncing, true)]
    [TestCase(PayloadStatus.Accepted, true)]
    [TestCase(PayloadStatus.Invalid, false)]
    public async Task Engine_driver_maps_statuses_and_bridges_the_transition_hook(string status, bool acceptable)
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_newPayloadV4(default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = status })));
        engine.engine_forkchoiceUpdatedV3(default!, default)
            .ReturnsForAnyArgs(Task.FromResult(ForkchoiceUpdatedV1Result.Valid(null, TestHash)));

        ExternalClDetector detector = CreateDetector(engine, out _);
        EngineDriver driver = new(detector, LimboLogs.Instance);
        SignedBeaconBlock block = CreateBlock();

        driver.CurrentBlock = block;
        PayloadStatusV1 newPayloadStatus = await driver.NewPayload(block);
        PayloadStatusV1 forkchoiceStatus = await driver.ForkchoiceUpdated(TestHash, TestHash, TestHash);

        Assert.Multiple(() =>
        {
            Assert.That(newPayloadStatus.Status, Is.EqualTo(status));
            Assert.That(driver.NotifyNewPayload(block.Message!.Body!), Is.EqualTo(acceptable));
            Assert.That(forkchoiceStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(detector.IsExternalClDetected, Is.False, "driver calls must not trip external-CL detection");
        });
    }

    [Test]
    public async Task Decorator_detects_external_engine_calls_but_not_capability_queries_or_driver_calls()
    {
        IEngineRpcModule inner = Substitute.For<IEngineRpcModule>();
        inner.engine_forkchoiceUpdatedV3(default!, default).ReturnsForAnyArgs(Task.FromResult(ForkchoiceUpdatedV1Result.Syncing));

        ContainerBuilder builder = new ContainerBuilder()
            .AddSingleton(inner)
            .AddSingleton<IBeaconChainConfig>(new BeaconChainConfig { Enabled = true })
            .AddSingleton<ILogManager>(LimboLogs.Instance)
            .AddSingleton<ExternalClDetector>()
            .AddDecorator<IEngineRpcModule, ExternalClInterceptingEngineRpcModule>();
        using IContainer container = builder.Build();

        IEngineRpcModule decorated = container.Resolve<IEngineRpcModule>();
        ExternalClDetector detector = container.Resolve<ExternalClDetector>();
        int detections = 0;
        detector.ExternalClDetected += () => detections++;

        decorated.engine_exchangeCapabilities([]);
        await detector.InnerEngine.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(TestHash, TestHash, TestHash));
        bool detectedBeforeExternalCall = detector.IsExternalClDetected;

        await decorated.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(TestHash, TestHash, TestHash));
        await decorated.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(TestHash, TestHash, TestHash));

        Assert.Multiple(() =>
        {
            Assert.That(decorated, Is.InstanceOf<ExternalClInterceptingEngineRpcModule>());
            Assert.That(detectedBeforeExternalCall, Is.False);
            Assert.That(detector.IsExternalClDetected, Is.True);
            Assert.That(detections, Is.EqualTo(1), "detection must latch and fire once");
            inner.ReceivedWithAnyArgs(3).engine_forkchoiceUpdatedV3(default!, default);
        });
    }

    // ---- Gloas execution payload envelopes go through engine_newPayloadV5 ----

    [Test]
    public void Payload_converter_maps_a_gloas_payload_onto_v4_and_encodes_builder_requests()
    {
        ExecutionPayloadGloas payload = new()
        {
            ParentHash = TestHash,
            FeeRecipient = Address.SystemUser,
            StateRoot = TestHash,
            ReceiptsRoot = TestHash,
            LogsBloom = Bloom.Empty,
            PrevRandao = TestHash,
            BlockNumber = 321,
            GasLimit = 36_000_000,
            GasUsed = 42_000,
            Timestamp = 1_800_000_000,
            ExtraData = Bytes.FromHexString("0xbeef"),
            BaseFeePerGas = 9,
            BlockHash = TestHash,
            Transactions = [new TransactionGloas { Bytes = Bytes.FromHexString("0x04cafe") }],
            Withdrawals = [new BeaconWithdrawal { Index = 5, ValidatorIndex = 6, Address = Address.SystemUser, Amount = 7 }],
            BlobGasUsed = 262144,
            ExcessBlobGas = 393216,
            BlockAccessList = Bytes.FromHexString("0xc0ffee"),
            SlotNumber = 77,
        };
        BuilderDepositRequest builderDeposit = new()
        {
            Pubkey = new BlsPublicKey(Enumerable.Repeat((byte)0x05, 48).ToArray()),
            WithdrawalCredentials = TestHash,
            Amount = 64_000_000_000,
            Signature = new BlsSignature(Enumerable.Repeat((byte)0x06, 96).ToArray()),
        };
        BuilderExitRequest builderExit = new() { SourceAddress = Address.SystemUser, Pubkey = builderDeposit.Pubkey };

        ExecutionPayloadV4 converted = PayloadConverter.ToExecutionPayloadV4(payload);
        byte[][] requests = PayloadConverter.ToExecutionRequestsList(new ExecutionRequestsGloas
        {
            Deposits = [],
            Withdrawals = [],
            Consolidations = [],
            BuilderDeposits = [builderDeposit],
            BuilderExits = [builderExit],
        });

        // EIP-8282: 0x03 || pubkey || withdrawal_credentials || amount || signature, 0x04 || source_address || pubkey
        byte[] expectedBuilderDeposit = new byte[1 + 48 + 32 + 8 + 96];
        expectedBuilderDeposit[0] = 0x03;
        builderDeposit.Pubkey.Bytes.CopyTo(expectedBuilderDeposit.AsSpan(1));
        TestHash.Bytes.CopyTo(expectedBuilderDeposit.AsSpan(1 + 48));
        BinaryPrimitives.WriteUInt64LittleEndian(expectedBuilderDeposit.AsSpan(1 + 80), builderDeposit.Amount);
        builderDeposit.Signature.Bytes.CopyTo(expectedBuilderDeposit.AsSpan(1 + 88));
        byte[] expectedBuilderExit = new byte[1 + 20 + 48];
        expectedBuilderExit[0] = 0x04;
        Address.SystemUser.Bytes.CopyTo(expectedBuilderExit.AsSpan(1));
        builderExit.Pubkey.Bytes.CopyTo(expectedBuilderExit.AsSpan(1 + 20));

        Assert.Multiple(() =>
        {
            Assert.That(converted.BlockNumber, Is.EqualTo(321));
            Assert.That(converted.GasLimit, Is.EqualTo(36_000_000));
            Assert.That(converted.Timestamp, Is.EqualTo(1_800_000_000));
            Assert.That(converted.BlockHash, Is.EqualTo(TestHash));
            Assert.That(converted.Transactions, Is.EqualTo(new[] { Bytes.FromHexString("0x04cafe") }));
            Assert.That(converted.Withdrawals![0].AmountInGwei, Is.EqualTo(7ul));
            Assert.That(converted.BlobGasUsed, Is.EqualTo(262144ul));
            Assert.That(converted.ExcessBlobGas, Is.EqualTo(393216ul));
            Assert.That(converted.BlockAccessList, Is.EqualTo(Bytes.FromHexString("0xc0ffee")), "the access list bytes must reach the execution layer unchanged");
            Assert.That(converted.SlotNumber, Is.EqualTo(77ul));
            Assert.That(requests, Has.Length.EqualTo(2), "empty request lists are omitted, the two builder lists are not");
            Assert.That(requests[0], Is.EqualTo(expectedBuilderDeposit));
            Assert.That(requests[1], Is.EqualTo(expectedBuilderExit));
        });
    }

    [TestCase(PayloadStatus.Valid, true)]
    [TestCase(PayloadStatus.Syncing, true)]
    [TestCase(PayloadStatus.Accepted, true)]
    [TestCase(PayloadStatus.Invalid, false)]
    public void Engine_driver_notifies_a_gloas_envelope_payload_through_newPayloadV5_and_maps_its_status(string status, bool acceptable)
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_newPayloadV5(default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = status })));
        ExternalClDetector detector = CreateDetector(engine, out _);
        EngineDriver driver = new(detector, LimboLogs.Instance);
        Hash256 parentBeaconBlockRoot = new("0x2222222222222222222222222222222222222222222222222222222222222222");
        Hash256?[] versionedHashes = [new Hash256("0x0133333333333333333333333333333333333333333333333333333333333333")];
        ExecutionRequestsGloas requests = new() { BuilderExits = [new BuilderExitRequest { SourceAddress = Address.SystemUser, Pubkey = new BlsPublicKey(new byte[48]) }] };

        // No CurrentBlock is set: an envelope carries its own parent beacon block root, so the
        // driver must not reach for the block the Fulu path needs.
        bool accepted = driver.NotifyNewPayload(GloasPayload(slotNumber: 91), versionedHashes, parentBeaconBlockRoot, requests);

        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.EqualTo(acceptable));
            Assert.That(driver.LastNewPayloadStatus!.Status, Is.EqualTo(status));
            Assert.That(detector.IsExternalClDetected, Is.False, "driver calls must not trip external-CL detection");
            engine.Received(1).engine_newPayloadV5(
                Arg.Is<ExecutionPayloadV4>(p => p.BlockHash == TestHash && p.SlotNumber == 91ul && p.BlockAccessList!.Length == 3),
                Arg.Is<Hash256?[]>(h => h.Length == 1 && h[0] == versionedHashes[0]),
                parentBeaconBlockRoot,
                Arg.Is<byte[][]>(r => r.Length == 1 && r[0][0] == 0x04));
            engine.DidNotReceiveWithAnyArgs().engine_newPayloadV4(default!, default!, default, default);
        });
    }

    [Test]
    public void Engine_driver_treats_a_failed_newPayloadV5_call_as_syncing_not_as_a_verdict()
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_newPayloadV5(default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(ResultWrapper<PayloadStatusV1>.Fail("engine unavailable")));
        EngineDriver driver = new(CreateDetector(engine, out _), LimboLogs.Instance);

        bool accepted = driver.NotifyNewPayload(GloasPayload(slotNumber: 5), [], TestHash, new ExecutionRequestsGloas());

        Assert.That(accepted, Is.True, "an engine error is not INVALID; the caller proceeds optimistically");
        Assert.That(driver.LastNewPayloadStatus!.Status, Is.EqualTo(PayloadStatus.Syncing));
    }

    /// <summary>
    /// The production notifier is <see cref="EngineDriver"/>; a Gloas node whose notifier still uses
    /// the interface's throwing default would only find out at its first envelope. This is the check
    /// the startup step runs, pinned so removing the driver's override fails here and not on Sepolia.
    /// </summary>
    [Test]
    public void RequireEnvelopeSupport_accepts_the_engine_driver_and_refuses_a_notifier_that_only_handles_block_bodies()
    {
        EngineDriver driver = new(CreateDetector(Substitute.For<IEngineRpcModule>(), out _), LimboLogs.Instance);

        Assert.That(() => INewPayloadNotifier.RequireEnvelopeSupport(driver), Throws.Nothing);
        Assert.That(() => INewPayloadNotifier.RequireEnvelopeSupport(new BodyOnlyNotifier()),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains(nameof(BodyOnlyNotifier)).And.Message.Contains("Gloas execution payload envelopes"));
    }

    private static ExecutionPayloadGloas GloasPayload(ulong slotNumber) => new()
    {
        ParentHash = TestHash,
        FeeRecipient = Address.SystemUser,
        StateRoot = TestHash,
        ReceiptsRoot = TestHash,
        LogsBloom = Bloom.Empty,
        PrevRandao = TestHash,
        BlockHash = TestHash,
        BaseFeePerGas = 1,
        ExtraData = [],
        Transactions = [],
        Withdrawals = [],
        BlockAccessList = [0xc0, 0xff, 0xee],
        SlotNumber = slotNumber,
    };

    private sealed class BodyOnlyNotifier : INewPayloadNotifier
    {
        public bool NotifyNewPayload(BeaconBlockBody body) => true;
    }

    private static ExternalClDetector CreateDetector(IEngineRpcModule inner, out ExternalClInterceptingEngineRpcModule decorator)
    {
        ExternalClDetector detector = new(new BeaconChainConfig { Enabled = true }, new Lazy<IEngineRpcModule>(inner), LimboLogs.Instance);
        decorator = new ExternalClInterceptingEngineRpcModule(inner, detector);
        return detector;
    }

    private static SignedBeaconBlock CreateBlock() => new()
    {
        Message = new BeaconBlock
        {
            Slot = 1,
            ParentRoot = TestHash,
            StateRoot = TestHash,
            Body = new BeaconBlockBody
            {
                ExecutionPayload = new BeaconExecutionPayload
                {
                    ParentHash = TestHash,
                    FeeRecipient = Address.SystemUser,
                    StateRoot = TestHash,
                    ReceiptsRoot = TestHash,
                    LogsBloom = Bloom.Empty,
                    PrevRandao = TestHash,
                    BlockHash = TestHash,
                    BaseFeePerGas = 1,
                    ExtraData = [],
                    Transactions = [],
                    Withdrawals = [],
                },
            },
        },
        Signature = new BlsSignature(new byte[96]),
    };
}
