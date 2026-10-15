// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Security.Cryptography;
using Autofac;
using Nethermind.BeaconChain.Engine;
using Nethermind.BeaconChain.ForkChoice;
using Nethermind.BeaconChain.StateTransition;
using Nethermind.BeaconChain.Types;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Blockchain;
using Nethermind.BeaconChain.Sync;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Logging;
using Nethermind.Merge.Plugin;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using NSubstitute;
using NSubstitute.Extensions;
using BeaconExecutionPayload = Nethermind.BeaconChain.Types.ExecutionPayload;
using BeaconTransaction = Nethermind.Merge.Plugin.SszRest.SszTransaction;
using BeaconWithdrawal = Nethermind.Merge.Plugin.SszRest.SszWithdrawal;

namespace Nethermind.BeaconChain.Test.Engine;

public class EngineTests
{
    private static readonly Hash256 TestHash = new("0x1111111111111111111111111111111111111111111111111111111111111111");

    [Test]
    public async Task Canonical_heads_from_the_anchor_up_do_not_rewind_an_execution_head_ahead_of_them([Values(0, 2)] int importedPastAnchor)
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create();
        BlockHeader anchor = chain.BlockTree.Head!.Header;
        await chain.BuildSomeBlocks(3);
        Hash256 head = chain.BlockTree.Head!.Hash!;
        (EngineDriver driver, IEngineRpcModule inner) = HeadMovingDriver(chain, catchingUp: true);

        PayloadStatusV1 result = await driver.ForkchoiceUpdated(anchor.Hash!, anchor.Hash!, anchor.Hash!);
        for (ulong number = anchor.Number + 1; number <= anchor.Number + (ulong)importedPastAnchor; number++)
        {
            result = await driver.ForkchoiceUpdated(chain.BlockTree.FindBlockHash(number)!, anchor.Hash!, anchor.Hash!);
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(result.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(chain.BlockTree.Head.Hash, Is.EqualTo(head));
        Assert.That(inner.ReceivedCalls(), importedPastAnchor == 0 ? Is.Empty : Is.Not.Empty, "only the first update at the anchor is answered without the engine");
    }

    [Test]
    public async Task Canonical_ancestor_head_replaces_the_execution_head_only_while_the_consensus_head_catches_up([Values] bool catchingUp)
    {
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create();
        BlockHeader anchor = chain.BlockTree.Head!.Header;
        await chain.BuildSomeBlocks(3);
        BlockHeader head = chain.BlockTree.Head!.Header;
        BlockHeader parent = chain.BlockTree.FindHeader(head.ParentHash!)!;
        // The parent's payload is slots older than the wall clock, as is an EMPTY Gloas head's at the tip.
        (EngineDriver driver, _) = HeadMovingDriver(chain, catchingUp);

        await driver.ForkchoiceUpdated(parent.Hash!, anchor.Hash!, anchor.Hash!);

        Assert.That(chain.BlockTree.Head!.Hash, Is.EqualTo(catchingUp ? head.Hash : parent.Hash), "a head at the tip reaches the execution layer whatever its payload's age");
    }

    // Like the execution layer's handler, a requested head other than the current one is forced to be the head.
    private static (EngineDriver Driver, IEngineRpcModule Inner) HeadMovingDriver(BasicTestBlockchain chain, bool catchingUp)
    {
        IEngineRpcModule inner = Substitute.For<IEngineRpcModule>();
        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> MoveHead(ForkchoiceStateV1 state)
        {
            chain.BlockTree.TryUpdateMainChain(chain.BlockTree.FindHeader(state.HeadBlockHash)!, wereProcessed: true, forceUpdateHeadBlock: true);
            return Task.FromResult(ForkchoiceUpdatedV1Result.Valid(null, state.HeadBlockHash));
        }
        inner.engine_forkchoiceUpdatedV3(default!, default).ReturnsForAnyArgs(call => MoveHead(call.Arg<ForkchoiceStateV1>()));
        inner.engine_forkchoiceUpdatedV4(default!, default, default).ReturnsForAnyArgs(call => MoveHead(call.Arg<ForkchoiceStateV1>()));
        EngineDriver driver = new(CreateDetector(inner, out _), LimboLogs.Instance,
            new SlotClock(TestEngineDriver.Spec, Timestamper.Default), TestEngineDriver.Spec,
            new TestEngineDriver.FixedCustodySource(null), chain.BlockTree)
        { IsCatchingUp = () => catchingUp };
        return (driver, inner);
    }

    [Test]
    public void Payload_converter_maps_fields_versioned_hashes_and_requests([Values] bool sliced)
    {
        byte[] transaction = Bytes.FromHexString(sliced ? "0xff02abcdff" : "0x02abcd");
        ReadOnlyMemory<byte> transactionBytes = sliced ? transaction.AsMemory(1, 3) : transaction;
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
            ParentHash = CreateHash(0x11),
            FeeRecipient = Address.SystemUser,
            StateRoot = CreateHash(0x22),
            ReceiptsRoot = CreateHash(0x33),
            LogsBloom = new Bloom(Enumerable.Range(0, 256).Select(static i => (byte)i).ToArray()),
            PrevRandao = CreateHash(0x44),
            BlockNumber = 123,
            GasLimit = 30_000_000,
            GasUsed = 21_000,
            Timestamp = 1_700_000_000,
            ExtraData = Bytes.FromHexString("0xdeadbeef"),
            BaseFeePerGas = 7,
            BlockHash = CreateHash(0x55),
            Transactions = [new BeaconTransaction { Bytes = transactionBytes }],
            Withdrawals = [new BeaconWithdrawal { Index = 1, ValidatorIndex = 2, Address = new Address("0x2222222222222222222222222222222222222222"), Amount = 3 }],
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(converted.BlockNumber, Is.EqualTo(123));
        Assert.That(converted.GasLimit, Is.EqualTo(30_000_000));
        Assert.That(converted.Timestamp, Is.EqualTo(1_700_000_000));
        AssertAdditionalPayloadFields(converted, payload.GasUsed, payload.ExtraData, payload.BaseFeePerGas, payload.Withdrawals![0]);
        Assert.That(converted.Transactions, Is.EqualTo(new[] { Bytes.FromHexString("0x02abcd") }));
        Assert.That(ReferenceEquals(converted.Transactions[0], transaction), Is.EqualTo(!sliced));
        Assert.That(converted.BlobGasUsed, Is.EqualTo(131072ul));
        Assert.That(converted.ExcessBlobGas, Is.EqualTo(262144ul));
        Assert.That(versionedHashes.Single()!.Bytes.ToArray(), Is.EqualTo(expectedVersionedHash));
        Assert.That(requests, Has.Length.EqualTo(2));
        Assert.That(requests[0], Is.EqualTo(expectedDeposits));
        Assert.That(requests[1], Is.EqualTo(expectedConsolidations));
    }

    [TestCase(PayloadStatus.Valid, ExecutionStatus.Valid)]
    [TestCase(PayloadStatus.Syncing, ExecutionStatus.Optimistic)]
    [TestCase(PayloadStatus.Accepted, ExecutionStatus.Optimistic)]
    [TestCase(PayloadStatus.InclusionListUnsatisfied, ExecutionStatus.Optimistic)]
    [TestCase(PayloadStatus.Invalid, ExecutionStatus.Invalid)]
    public async Task Engine_driver_maps_statuses_and_bridges_the_transition_hook(string status, ExecutionStatus expected)
    {
        Hash256 latestValidHash = Keccak.Compute("latest valid");
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_newPayloadV4(default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = status, LatestValidHash = latestValidHash })));
        engine.engine_forkchoiceUpdatedV3(default!, default)
            .ReturnsForAnyArgs(Task.FromResult(ForkchoiceUpdatedV1Result.Valid(null, TestHash)));

        ExternalClDetector detector = CreateDetector(engine, out _);
        EngineDriver driver = TestEngineDriver.Create(detector);
        SignedBeaconBlock block = CreateBlock();

        driver.CurrentBlock = block;
        PayloadStatusV1 newPayloadStatus = await driver.NewPayload(block);
        PayloadStatusV1 forkchoiceStatus = await driver.ForkchoiceUpdated(TestHash, TestHash, TestHash);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(newPayloadStatus.Status, Is.EqualTo(status));
        Assert.That(driver.NotifyNewPayload(block.Message!.Body!), Is.EqualTo(expected));
        // specs/bellatrix/optimistic-sync.md: an INVALID status is applied through its latestValidHash, so the hook must not drop it.
        Assert.That(((IEngineDriver)driver).NotifyNewPayload(block.Message!.Body!, out Hash256? reportedHash), Is.EqualTo(expected));
        Assert.That(reportedHash, Is.EqualTo(latestValidHash));
        Assert.That(forkchoiceStatus.Status, Is.EqualTo(PayloadStatus.Valid));
        Assert.That(detector.IsExternalClDetected, Is.False, "driver calls must not trip external-CL detection");
    }

    [Test]
    public async Task A_failed_external_engine_call_does_not_stand_down_the_driver()
    {
        IEngineRpcModule inner = Substitute.For<IEngineRpcModule>();
        TaskCompletionSource<ResultWrapper<ForkchoiceUpdatedV1Result>> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        inner.engine_forkchoiceUpdatedV3(default!, default).ReturnsForAnyArgs(completion.Task);
        ExternalClDetector detector = CreateDetector(inner, out ExternalClInterceptingEngineRpcModule decorator);
        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> call = decorator.engine_forkchoiceUpdatedV3(new ForkchoiceStateV1(TestHash, TestHash, TestHash));
        Assert.That(detector.IsExternalClDetected, Is.False);
        completion.SetResult(ResultWrapper<ForkchoiceUpdatedV1Result>.Fail("unsupported fork"));
        await call;
        Assert.That(detector.IsExternalClDetected, Is.False);
    }

    [Test]
    public async Task A_witness_call_forwarded_back_through_the_decorator_does_not_stand_down_the_driver()
    {
        IEngineRpcModule inner = Substitute.For<IEngineRpcModule>();
        ExternalClDetector detector = CreateDetector(inner, out ExternalClInterceptingEngineRpcModule decorator);
        inner.engine_newPayloadV4(default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = PayloadStatus.Valid, LatestValidHash = TestHash })));
        // The witness handler resolves IEngineRpcModule from the container, which is the decorator itself.
        inner.engine_newPayloadWithWitnessV4(default!, default!, default, default)
            .ReturnsForAnyArgs(async _ =>
            {
                await decorator.engine_newPayloadV4(null!, [], null, null);
                return ResultWrapper<NewPayloadWithWitnessV1Result>.Success(new NewPayloadWithWitnessV1Result { Status = PayloadStatus.Valid });
            });

        await decorator.engine_newPayloadWithWitnessV4(null!, [], null, null);
        bool detectedAfterWitnessCall = detector.IsExternalClDetected;
        await decorator.engine_newPayloadV4(null!, [], null, null);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(detectedAfterWitnessCall, Is.False, "a witness submission is stateless tooling, not a consensus client");
        Assert.That(detector.IsExternalClDetected, Is.True, "a direct newPayload still counts");
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

    [Test]
    public void Payload_converter_maps_a_gloas_payload_onto_v4_and_encodes_builder_requests()
    {
        ExecutionPayloadGloas payload = new()
        {
            ParentHash = CreateHash(0x11),
            FeeRecipient = Address.SystemUser,
            StateRoot = CreateHash(0x22),
            ReceiptsRoot = CreateHash(0x33),
            LogsBloom = new Bloom(Enumerable.Range(0, 256).Select(static i => (byte)i).ToArray()),
            PrevRandao = CreateHash(0x44),
            BlockNumber = 321,
            GasLimit = 36_000_000,
            GasUsed = 42_000,
            Timestamp = 1_800_000_000,
            ExtraData = Bytes.FromHexString("0xbeef"),
            BaseFeePerGas = 9,
            BlockHash = CreateHash(0x55),
            Transactions = [new TransactionGloas { Bytes = Bytes.FromHexString("0x04cafe") }],
            Withdrawals = [new BeaconWithdrawal { Index = 5, ValidatorIndex = 6, Address = new Address("0x2222222222222222222222222222222222222222"), Amount = 7 }],
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

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(converted.BlockNumber, Is.EqualTo(321));
        Assert.That(converted.GasLimit, Is.EqualTo(36_000_000));
        Assert.That(converted.Timestamp, Is.EqualTo(1_800_000_000));
        AssertAdditionalPayloadFields(converted, payload.GasUsed, payload.ExtraData, payload.BaseFeePerGas, payload.Withdrawals![0]);
        Assert.That(converted.Transactions, Is.EqualTo(new[] { Bytes.FromHexString("0x04cafe") }));
        Assert.That(converted.Transactions[0], Is.SameAs(payload.Transactions[0].Bytes));
        Assert.That(converted.BlobGasUsed, Is.EqualTo(262144ul));
        Assert.That(converted.ExcessBlobGas, Is.EqualTo(393216ul));
        Assert.That(converted.BlockAccessList, Is.EqualTo(Bytes.FromHexString("0xc0ffee")), "the access list bytes must reach the execution layer unchanged");
        Assert.That(converted.SlotNumber, Is.EqualTo(77ul));
        Assert.That(requests, Has.Length.EqualTo(2), "empty request lists are omitted, the two builder lists are not");
        Assert.That(requests[0], Is.EqualTo(expectedBuilderDeposit));
        Assert.That(requests[1], Is.EqualTo(expectedBuilderExit));
    }

    [TestCase(PayloadStatus.Valid, ExecutionStatus.Valid)]
    [TestCase(PayloadStatus.Syncing, ExecutionStatus.Optimistic)]
    [TestCase(PayloadStatus.Accepted, ExecutionStatus.Optimistic)]
    [TestCase(PayloadStatus.InclusionListUnsatisfied, ExecutionStatus.Optimistic)]
    [TestCase(PayloadStatus.Invalid, ExecutionStatus.Invalid)]
    public void Engine_driver_notifies_a_gloas_envelope_payload_through_newPayloadV5_and_maps_its_status(string status, ExecutionStatus expected)
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        engine.engine_newPayloadV5(default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(ResultWrapper<PayloadStatusV1>.Success(new PayloadStatusV1 { Status = status })));
        ExternalClDetector detector = CreateDetector(engine, out _);
        EngineDriver driver = TestEngineDriver.Create(detector);
        Hash256 parentBeaconBlockRoot = new("0x2222222222222222222222222222222222222222222222222222222222222222");
        Hash256?[] versionedHashes = [new Hash256("0x0133333333333333333333333333333333333333333333333333333333333333")];
        ExecutionRequestsGloas requests = new() { BuilderExits = [new BuilderExitRequest { SourceAddress = Address.SystemUser, Pubkey = new BlsPublicKey(new byte[48]) }] };

        // No CurrentBlock is set: an envelope carries its own parent beacon block root, so the
        // driver must not reach for the block the Fulu path needs.
        ExecutionStatus verdict = driver.NotifyNewPayload(GloasPayload(slotNumber: 91), versionedHashes, parentBeaconBlockRoot, requests);

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(verdict, Is.EqualTo(expected));
        Assert.That(detector.IsExternalClDetected, Is.False, "driver calls must not trip external-CL detection");
        engine.Received(1).engine_newPayloadV5(
            Arg.Is<ExecutionPayloadV4>(p => p.BlockHash == TestHash && p.SlotNumber == 91ul && p.BlockAccessList!.Length == 3),
            Arg.Is<Hash256?[]>(h => h.Length == 1 && h[0] == versionedHashes[0]),
            parentBeaconBlockRoot,
            Arg.Is<byte[][]>(r => r.Length == 1 && r[0][0] == 0x04));
        engine.DidNotReceiveWithAnyArgs().engine_newPayloadV4(default!, default!, default, default);
    }

    [Test]
    public void Engine_driver_refuses_to_turn_a_failed_newPayloadV5_call_into_a_verdict()
    {
        EngineDriver driver = CreateSyncingDriver(out IEngineRpcModule engine);
        driver.NotifyNewPayload(GloasPayload(slotNumber: 5), [], TestHash, new ExecutionRequestsGloas());
        Assert.That(driver.IsAvailable, Is.True);
        engine.Configure().engine_newPayloadV5(default!, default!, default, default)
            .ReturnsForAnyArgs(Task.FromResult(ResultWrapper<PayloadStatusV1>.Fail("engine unavailable")));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(() => driver.NotifyNewPayload(GloasPayload(slotNumber: 5), [], TestHash, new ExecutionRequestsGloas()),
            Throws.TypeOf<EngineUnavailableException>(),
            "a failed call is not a verdict; reporting it as SYNCING made the caller accept the block");
        Assert.That(driver.IsAvailable, Is.False);
    }

    // Failed forkchoiceUpdated is not a SYNCING answer: caching it would prevent retry.
    [TestCase(0, TestName = "Failure result")]
    [TestCase(1, TestName = "Success with no data")]
    [TestCase(2, TestName = "Success with no payload status")]
    public async Task Engine_driver_refuses_to_turn_a_failed_forkchoiceUpdated_call_into_a_status(int shape)
    {
        ResultWrapper<ForkchoiceUpdatedV1Result> answer = shape switch
        {
            0 => ResultWrapper<ForkchoiceUpdatedV1Result>.Fail("engine unavailable"),
            1 => ResultWrapper<ForkchoiceUpdatedV1Result>.Success(null!),
            _ => ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result { PayloadStatus = null! }),
        };
        EngineDriver driver = CreateSyncingDriver(out IEngineRpcModule engine);
        await driver.ForkchoiceUpdated(TestHash, TestHash, TestHash);
        Assert.That(driver.IsAvailable, Is.True);
        engine.Configure().engine_forkchoiceUpdatedV3(default!, default).ReturnsForAnyArgs(Task.FromResult(answer));
        await Assert.ThrowsAsync<EngineUnavailableException>(() => driver.ForkchoiceUpdated(TestHash, TestHash, TestHash));

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(driver.IsAvailable, Is.False);
    }

    [Test]
    public void RequireEnvelopeSupport_accepts_the_engine_driver_and_refuses_a_notifier_that_only_handles_block_bodies()
    {
        EngineDriver driver = TestEngineDriver.Create(CreateDetector(Substitute.For<IEngineRpcModule>(), out _));

        Assert.That(() => INewPayloadNotifier.RequireEnvelopeSupport(driver), Throws.Nothing);
        Assert.That(() => INewPayloadNotifier.RequireEnvelopeSupport(new TestEngineDriver.BodyOnlyNotifier()),
            Throws.TypeOf<InvalidOperationException>().With.Message.Contains(nameof(TestEngineDriver.BodyOnlyNotifier)).And.Message.Contains("Gloas execution payload envelopes"));
    }

    [Test]
    public async Task Engine_faults_are_unavailable_and_a_later_call_recovers(
        [Values(0, 1, 2, 3)] int method, [Values] bool synchronous, [Values] bool cancellation)
    {
        Exception cause = cancellation ? new OperationCanceledException("engine budget") : new InvalidOperationException("storage failure");
        EngineDriver driver = CreateSyncingDriver(out IEngineRpcModule engine,
            method == 1 ? TestEngineDriver.Spec.GloasForkEpoch * TestEngineDriver.Spec.SlotsPerEpoch : 0);

        await InvokeEngine(driver, method);
        Assert.That(driver.IsAvailable, Is.True);
        ConfigureEngine(engine, synchronous ? () => throw cause : () => Task.FromException<PayloadStatusV1>(cause));
        EngineUnavailableException? failure = await Assert.ThrowsAsync<EngineUnavailableException>(() => InvokeEngine(driver, method));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(failure!.InnerException, cancellation ? Is.InstanceOf<OperationCanceledException>() : Is.SameAs(cause));
            Assert.That(driver.IsAvailable, Is.False);
        }

        ConfigureEngine(engine, () => Task.FromResult(PayloadStatusV1.Syncing));
        await InvokeEngine(driver, method);
        Assert.That(driver.IsAvailable, Is.True);
    }

    [Test]
    public async Task External_takeover_blocks_every_embedded_engine_method(
        [Values(0, 1, 2, 3)] int method, [Values] bool disableOnExternalCl)
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        ConfigureEngine(engine, () => Task.FromResult(PayloadStatusV1.Syncing));
        ExternalClDetector detector = new(new BeaconChainConfig { DisableOnExternalCl = disableOnExternalCl },
            new Lazy<IEngineRpcModule>(engine), LimboLogs.Instance);
        detector.SetInner(engine);
        EngineDriver driver = TestEngineDriver.Create(detector,
            method == 1 ? TestEngineDriver.Spec.GloasForkEpoch * TestEngineDriver.Spec.SlotsPerEpoch : 0);
        detector.OnExternalEngineCall();
        engine.ClearReceivedCalls();

        if (disableOnExternalCl)
            await Assert.ThrowsAsync<OperationCanceledException>(() => InvokeEngine(driver, method));
        else
            await InvokeEngine(driver, method);

        Assert.That(engine.ReceivedCalls().Count(), Is.EqualTo(disableOnExternalCl ? 0 : 1));
    }

    [Test]
    public async Task Forkchoice_deadline_releases_the_caller_without_overlapping_calls([Values] bool synchronous)
    {
        IEngineRpcModule engine = Substitute.For<IEngineRpcModule>();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource returned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<PayloadStatusV1> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using ManualResetEventSlim release = new();
        ConfigureEngine(engine, () =>
        {
            entered.TrySetResult();
            if (synchronous) release.Wait();
            returned.TrySetResult();
            return answer.Task;
        });
        EngineDriver driver = TestEngineDriver.Create(CreateDetector(engine, out _), forkchoiceTimeout: TimeSpan.FromMilliseconds(50));
        Task<PayloadStatusV1> call = driver.ForkchoiceUpdated(TestHash, TestHash, TestHash);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            EngineUnavailableException? timeout = await Assert.ThrowsAsync<EngineUnavailableException>(() => call.WaitAsync(TimeSpan.FromSeconds(2)));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(timeout!.InnerException, Is.InstanceOf<TimeoutException>());
                Assert.That(driver.IsAvailable, Is.False);
            }

            await Assert.ThrowsAsync<EngineUnavailableException>(() => driver.ForkchoiceUpdated(TestHash, TestHash, TestHash).WaitAsync(TimeSpan.FromSeconds(2)));
            await Assert.ThrowsAsync<EngineUnavailableException>(() => Task.Run(() => driver.NewPayload(CreateBlock())).WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.That(engine.ReceivedCalls().Count(), Is.EqualTo(1));
        }
        finally
        {
            answer.TrySetResult(PayloadStatusV1.Syncing);
            release.Set();
            if (entered.Task.IsCompleted) await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static Task<PayloadStatusV1> InvokeEngine(EngineDriver driver, int method) => method switch
    {
        0 or 1 => driver.ForkchoiceUpdated(TestHash, TestHash, TestHash),
        2 => driver.NewPayload(CreateBlock()),
        _ => driver.NewPayload(GloasPayload(5), [], TestHash, new ExecutionRequestsGloas()),
    };

    internal static EngineDriver CreateSyncingDriver(out IEngineRpcModule engine, ulong slot = 0)
    {
        engine = Substitute.For<IEngineRpcModule>();
        ConfigureEngine(engine, static () => Task.FromResult(PayloadStatusV1.Syncing));
        return TestEngineDriver.Create(CreateDetector(engine, out _), slot);
    }

    internal static void ConfigureEngine(IEngineRpcModule engine, Func<Task<PayloadStatusV1>> response)
    {
        engine.Configure().engine_newPayloadV4(default!, default!, default, default)
            .ReturnsForAnyArgs(_ => PayloadResult());
        engine.Configure().engine_newPayloadV5(default!, default!, default, default)
            .ReturnsForAnyArgs(_ => PayloadResult());
        engine.Configure().engine_forkchoiceUpdatedV3(default!, default)
            .ReturnsForAnyArgs(_ => ForkchoiceResult());
        engine.Configure().engine_forkchoiceUpdatedV4(default!, default, default)
            .ReturnsForAnyArgs(_ => ForkchoiceResult());

        Task<ResultWrapper<PayloadStatusV1>> PayloadResult()
        {
            Task<PayloadStatusV1> task = response();
            return WrapAsync();
            async Task<ResultWrapper<PayloadStatusV1>> WrapAsync() => ResultWrapper<PayloadStatusV1>.Success(await task);
        }

        Task<ResultWrapper<ForkchoiceUpdatedV1Result>> ForkchoiceResult()
        {
            Task<PayloadStatusV1> task = response();
            return WrapAsync();
            async Task<ResultWrapper<ForkchoiceUpdatedV1Result>> WrapAsync() =>
                ResultWrapper<ForkchoiceUpdatedV1Result>.Success(new ForkchoiceUpdatedV1Result { PayloadStatus = await task });
        }
    }

    private static Hash256 CreateHash(byte value) => new(Enumerable.Repeat(value, Hash256.Size).ToArray());

    private static void AssertAdditionalPayloadFields(ExecutionPayloadV3 actual, ulong gasUsed, byte[]? extraData, UInt256 baseFeePerGas, BeaconWithdrawal withdrawal)
    {
        using (Assert.EnterMultipleScope())
        {
            Assert.That(actual.ParentHash, Is.EqualTo(CreateHash(0x11)));
            Assert.That(actual.FeeRecipient, Is.EqualTo(Address.SystemUser));
            Assert.That(actual.StateRoot, Is.EqualTo(CreateHash(0x22)));
            Assert.That(actual.ReceiptsRoot, Is.EqualTo(CreateHash(0x33)));
            Assert.That(actual.LogsBloom, Is.EqualTo(new Bloom(Enumerable.Range(0, 256).Select(static i => (byte)i).ToArray())));
            Assert.That(actual.PrevRandao, Is.EqualTo(CreateHash(0x44)));
            Assert.That(actual.GasUsed, Is.EqualTo(gasUsed));
            Assert.That(actual.ExtraData, Is.EqualTo(extraData));
            Assert.That(actual.BaseFeePerGas, Is.EqualTo(baseFeePerGas));
            Assert.That(actual.BlockHash, Is.EqualTo(CreateHash(0x55)));
            Assert.That(actual.Withdrawals, Has.Length.EqualTo(1));
        }

        using IDisposable assertionScope = Assert.EnterMultipleScope();
        Assert.That(actual.Withdrawals![0].Index, Is.EqualTo(withdrawal.Index));
        Assert.That(actual.Withdrawals[0].ValidatorIndex, Is.EqualTo(withdrawal.ValidatorIndex));
        Assert.That(actual.Withdrawals[0].Address, Is.EqualTo(withdrawal.Address));
        Assert.That(actual.Withdrawals[0].AmountInGwei, Is.EqualTo(withdrawal.Amount));
    }

    internal static ExecutionPayloadGloas GloasPayload(ulong slotNumber) => new()
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

    internal static ExternalClDetector CreateDetector(IEngineRpcModule inner, out ExternalClInterceptingEngineRpcModule decorator)
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
