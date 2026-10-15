// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>EIP-7805 omission claims and separate inclusion lists over the engine API.</summary>
/// <remarks>The frame sender approves only once its slot 0 is set, which the list's second entry does, so the
/// builder's attempt at index 0 fails while the candidate is eligible at the end of the payload.</remarks>
public partial class EngineModuleTests
{
    private static readonly Address Profile2Sender = TestItem.AddressD;

    [TestCase(true, TestName = "Claim from getPayloadV7 excuses the omission through newPayloadV6")]
    [TestCase(false, TestName = "Without the claim the omission is judged at the end of the payload")]
    public async Task Claimed_index_travels_from_getPayload_through_newPayloadV6_to_the_verdict(bool sendClaims)
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        (GetPayloadV7Result built, byte[] frameTx, byte[] enabler, _) = await BuildProfile2Payload(chain);
        ExecutionPayloadV4 payload = built.ExecutionPayload;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.Transactions, Is.EqualTo(new[] { enabler }), "only the enabling entry was appended");
            Assert.That(built.OmissionClaims, Is.EqualTo(new[] { new OmissionClaim(Keccak.Compute(frameTx), 0) }));
        }

        ResultWrapper<PayloadStatusV2> newPayload = await rpc.engine_newPayloadV6(
            payload, [], Keccak.Zero, built.ExecutionRequests, AtPositions([frameTx, enabler]), sendClaims ? built.OmissionClaims : []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(newPayload.Result.ResultType, Is.EqualTo(ResultType.Success), newPayload.Result.Error);
            Assert.That(newPayload.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(await InclusionVerdict(rpc, newPayload.Data), Is.EqualTo(sendClaims));
        }
    }

    /// <summary>newPayloadWithWitnessV6 carries the lists and claims as newPayloadV6 does. Its response has no
    /// identifier, but the identifier is a function of the request, so the check it scheduled can still be polled.</summary>
    [TestCase(true, TestName = "Claims sent through newPayloadWithWitnessV6 excuse the omission")]
    [TestCase(false, TestName = "Without claims newPayloadWithWitnessV6 judges the omission at the end of the payload")]
    public async Task NewPayloadWithWitnessV6_carries_inclusion_lists_and_claims(bool sendClaims)
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        (GetPayloadV7Result built, byte[] frameTx, byte[] enabler, _) = await BuildProfile2Payload(chain);
        ExecutionPayloadV4 payload = built.ExecutionPayload;
        byte[][][] inclusionLists = AtPositions([frameTx, enabler]);
        OmissionClaim[] claims = sendClaims ? built.OmissionClaims : [];

        ResultWrapper<NewPayloadWithWitnessV1Result> newPayload = await rpc.engine_newPayloadWithWitnessV6(
            payload, [], Keccak.Zero, built.ExecutionRequests, inclusionLists, claims);
        PayloadStatusV2 polled = new() { InclusionCheckId = InclusionCheckId.Compute(payload.BlockHash, inclusionLists, claims) };

        using (Assert.EnterMultipleScope())
        {
            Assert.That(newPayload.Data.Status, Is.EqualTo(PayloadStatus.Valid), newPayload.Result.Error);
            Assert.That(await InclusionVerdict(rpc, polled), Is.EqualTo(sendClaims));
        }
    }

    /// <summary>Claims change the verdict, so the same block and lists under other claims are another check.</summary>
    [Test]
    public async Task NewPayloadV6_does_not_reuse_a_verdict_checked_under_other_claims()
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        (GetPayloadV7Result built, byte[] frameTx, byte[] enabler, _) = await BuildProfile2Payload(chain);
        byte[][][] inclusionLists = AtPositions([frameTx, enabler]);

        ResultWrapper<PayloadStatusV2> claimed = await rpc.engine_newPayloadV6(built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, inclusionLists, built.OmissionClaims);
        bool? claimedVerdict = await InclusionVerdict(rpc, claimed.Data);
        // Canonical, so the resend is answered from the result cache or the committed state, not re-executed.
        Hash256 hash = built.ExecutionPayload.BlockHash;
        await rpc.engine_forkchoiceUpdatedV5(new ForkchoiceStateV1(hash, hash, hash), payloadAttributes: null);
        ResultWrapper<PayloadStatusV2> unclaimed = await rpc.engine_newPayloadV6(built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, inclusionLists, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unclaimed.Data.InclusionCheckId, Is.Not.EqualTo(claimed.Data.InclusionCheckId));
            Assert.That(claimedVerdict, Is.True);
            Assert.That(await InclusionVerdict(rpc, unclaimed.Data), Is.False);
        }
    }

    /// <summary>EIP-7805 § VERIFY budget over the wire: the stuffing and the candidate overrun one committee position's
    /// budget and excuse the candidate, while at two positions each has its own budget and the candidate is judged.</summary>
    [TestCase(true, true, TestName = "Inclusion lists fill the VERIFY budget per committee position")]
    [TestCase(false, false, TestName = "Entries at separate committee positions draw on separate budgets")]
    public async Task NewPayloadV6_fills_the_verify_budget_per_position(bool samePosition, bool satisfied)
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        // Each declares 250,000 of VERIFY gas, under MAX_VERIFY_GAS_PER_TX, so four fill a position's 1,048,576 and
        // a fifth overruns it; the stuffing's senders are picked so that their hashes, and so their turns, come first.
        byte[] candidate = Encode(Profile2FrameTx(chain, Profile2Sender, 250_000));
        byte[][] stuffing = [.. TestItem.Addresses.Where(a => a != Profile2Sender).Select(a => Encode(Profile2FrameTx(chain, a, 250_000)))
            .Where(tx => Keccak.Compute(tx).CompareTo(Keccak.Compute(candidate)) < 0).Take(4)];
        byte[] enabler = Encode(Profile2Enabler());
        // One payload, built with all of them at one position so the candidate is tried first and omitted.
        (GetPayloadV7Result built, _) = await BuildWithFcu(chain, BuildBogotaPayloadAttributes(AtPositions([.. stuffing, candidate, enabler])));
        byte[][][] inclusionLists = samePosition ? AtPositions([.. stuffing, candidate, enabler]) : AtPositions([.. stuffing, enabler], [candidate]);
        Assume.That(built.ExecutionPayload.Transactions, Is.EqualTo(new[] { enabler }));

        ResultWrapper<PayloadStatusV2> newPayload = await chain.EngineRpcModule.engine_newPayloadV6(
            built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, inclusionLists, []);

        Assert.That(await InclusionVerdict(chain.EngineRpcModule, newPayload.Data), Is.EqualTo(satisfied), newPayload.Result.Error);
    }

    /// <summary>The claims and lists round-trip the JSON wire: getPayloadV7 writes the claims, newPayloadV6 parses
    /// them back beside the separate lists and answers with the check's identifier.</summary>
    [Test]
    public async Task Claims_and_inclusion_lists_round_trip_the_json_wire()
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        (GetPayloadV7Result built, byte[] frameTx, byte[] enabler, string payloadId) = await BuildProfile2Payload(chain);

        using JsonDocument getPayload = JsonDocument.Parse(await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV7", payloadId));
        JsonElement result = getPayload.RootElement.GetProperty("result");
        JsonElement claims = result.GetProperty("omissionClaims");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claims.GetArrayLength(), Is.EqualTo(1));
            Assert.That(claims[0].GetProperty("transactionHash").GetString(), Is.EqualTo(Keccak.Compute(frameTx).ToString()));
            Assert.That(claims[0].GetProperty("evaluationIndex").GetString(), Is.EqualTo("0x0"));
        }

        using JsonDocument newPayload = JsonDocument.Parse(await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV6",
            result.GetProperty("executionPayload"), System.Array.Empty<string>(), Keccak.Zero, result.GetProperty("executionRequests"), AtPositions([frameTx, enabler]), claims));
        JsonElement status = newPayload.RootElement.GetProperty("result");
        Hash256 expectedId = InclusionCheckId.Compute(built.ExecutionPayload.BlockHash, AtPositions([frameTx, enabler]), built.OmissionClaims);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(status.GetProperty("status").GetString(), Is.EqualTo(PayloadStatus.Valid));
            Assert.That(status.TryGetProperty("inclusionListSatisfied", out _), Is.True, "the verdict is always on the wire, null while pending");
            Assert.That(status.GetProperty("inclusionCheckId").GetString(), Is.EqualTo(expectedId.ToString()));
            Assert.That(await InclusionVerdict(rpc, new PayloadStatusV2 { InclusionCheckId = expectedId }), Is.True);
        }
    }

    /// <summary>Claims are Bogota's alone: getPayloadV6 no longer serves a Bogota payload, and getPayloadV7 serves
    /// no earlier one, each answering -38005 as a getPayload version outside its fork does.</summary>
    [TestCase(true, 6, false, TestName = "GetPayloadV6 rejects a Bogota payload as an unsupported fork")]
    [TestCase(true, 7, true, TestName = "GetPayloadV7 serves a Bogota payload")]
    [TestCase(false, 7, false, TestName = "GetPayloadV7 rejects an Amsterdam payload as an unsupported fork")]
    [TestCase(false, 6, true, TestName = "GetPayloadV6 serves an Amsterdam payload")]
    public async Task GetPayload_version_is_gated_by_fork(bool bogota, int version, bool served)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(bogota ? Bogota.Instance : Amsterdam.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        Hash256 head = chain.BlockTree.HeadHash;
        PayloadAttributes attributes = BuildBogotaPayloadAttributes(inclusionList: bogota ? [] : null!);
        ForkchoiceStateV1 state = new(head, Keccak.Zero, head);
        string? id = bogota
            ? (await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(state, attributes)).Data.PayloadId
            : (await chain.EngineRpcModule.engine_forkchoiceUpdatedV4(state, attributes)).Data.PayloadId;
        byte[] payloadId = Bytes.FromHexString(id!);

        int errorCode = version == 7
            ? (await chain.EngineRpcModule.engine_getPayloadV7(payloadId)).ErrorCode
            : (await chain.EngineRpcModule.engine_getPayloadV6(payloadId)).ErrorCode;

        Assert.That(errorCode, Is.EqualTo(served ? ErrorCodes.None : MergeErrorCodes.UnsupportedFork));
    }

    /// <summary>bogota.md makes the separate lists and claims positional in Bogota, with or without EIP-8141.</summary>
    [TestCase(true, TestName = "Inclusion lists are required when EIP-8141 frames are active")]
    [TestCase(false, TestName = "Inclusion lists are required under EIP-7805 alone")]
    public async Task Inclusion_lists_are_required_with_or_without_frame_transactions(bool frames)
    {
        using MergeTestBlockchain chain = frames ? await CreateProfile2Blockchain() : await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        Hash256 head = chain.BlockTree.HeadHash;
        (GetPayloadV7Result built, _) = await BuildWithFcu(chain, BuildBogotaPayloadAttributes(inclusionList: []));

        ResultWrapper<PayloadStatusV2> withoutLists = await chain.EngineRpcModule.engine_newPayloadV6(
            built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, null, []);
        ResultWrapper<PayloadStatusV2> withoutClaims = await chain.EngineRpcModule.engine_newPayloadV6(
            built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, AtPositions(), null);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(head, Keccak.Zero, head), BuildBogotaPayloadAttributes(inclusionList: null!));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(withoutLists.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams), withoutLists.Result.Error);
            Assert.That(withoutClaims.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams), withoutClaims.Result.Error);
            Assert.That(fcu.ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidPayloadAttributes), fcu.Result.Error);
        }
    }

    /// <summary>The builder fills each committee position's VERIFY budget apart, in ascending hash order: the
    /// stuffed position admits four of its five entries and the fifth gets no claim, while the other position's
    /// candidate keeps its claim however the first spends.</summary>
    [Test]
    public async Task ForkchoiceUpdatedV5_builder_claims_per_committee_position()
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        // 250,000 each, under MAX_VERIFY_GAS_PER_TX: four fit one position's 1,048,576, a fifth does not; pooled, the
        // four would exhaust the budget.
        byte[][] stuffing = [.. TestItem.Addresses.Where(a => a != Profile2Sender).Take(5).Select(a => Encode(Profile2FrameTx(chain, a, 250_000)))];
        byte[] frameBytes = Encode(Profile2FrameTx(chain, Profile2Sender, 100_000));
        byte[] enablerBytes = Encode(Profile2Enabler());
        byte[][][] inclusionLists = AtPositions(stuffing, [frameBytes, enablerBytes]);
        Hash256[] admittedStuffing = [.. stuffing.Select(Keccak.Compute).Order().Take(4)];

        (GetPayloadV7Result built, _) = await BuildWithFcu(chain, BuildBogotaPayloadAttributes(inclusionLists));
        ResultWrapper<PayloadStatusV2> newPayload = await chain.EngineRpcModule.engine_newPayloadV6(
            built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, inclusionLists, built.OmissionClaims);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built.ExecutionPayload.Transactions, Is.EqualTo(new[] { enablerBytes }));
            Assert.That(built.OmissionClaims, Is.EquivalentTo(
                admittedStuffing.Select(h => new OmissionClaim(h, 0)).Append(new OmissionClaim(Keccak.Compute(frameBytes), 0))));
            Assert.That(await InclusionVerdict(chain.EngineRpcModule, newPayload.Data), Is.True, newPayload.Result.Error);
        }
    }

    /// <summary>PayloadAttributesV5 parses the separate lists from the wire, and they are part of the build's identity:
    /// the same entries at other committee positions are a different build, since positions change the claims.</summary>
    [Test]
    public async Task ForkchoiceUpdatedV5_parses_inclusion_lists_into_a_distinct_build()
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        byte[] frameBytes = Encode(Profile2FrameTx(chain, Profile2Sender, 100_000));
        byte[] enablerBytes = Encode(Profile2Enabler());
        Hash256 head = chain.BlockTree.HeadHash;
        PayloadAttributes flat = BuildBogotaPayloadAttributes(inclusionList: []);

        string[][] inclusionLists = [[frameBytes.ToHexString(true)], [enablerBytes.ToHexString(true)], .. Enumerable.Repeat(System.Array.Empty<string>(), Eip7805Constants.InclusionListCommitteeSize - 2)];
        using JsonDocument parsed = JsonDocument.Parse(await RpcTest.TestSerializedRequest(chain.EngineRpcModule, "engine_forkchoiceUpdatedV5",
            new ForkchoiceStateV1(head, Keccak.Zero, head),
            new
            {
                timestamp = flat.Timestamp.ToHexString(true),
                prevRandao = flat.PrevRandao,
                suggestedFeeRecipient = flat.SuggestedFeeRecipient,
                withdrawals = System.Array.Empty<Withdrawal>(),
                parentBeaconBlockRoot = flat.ParentBeaconBlockRoot,
                slotNumber = flat.SlotNumber!.Value.ToHexString(true),
                targetGasLimit = flat.TargetGasLimit!.Value.ToHexString(true),
                inclusionLists,
            }));
        ResultWrapper<ForkchoiceUpdatedV1Result> samePositions = await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(head, Keccak.Zero, head), BuildBogotaPayloadAttributes(AtPositions([frameBytes], [enablerBytes])));
        ResultWrapper<ForkchoiceUpdatedV1Result> otherPositions = await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(head, Keccak.Zero, head), BuildBogotaPayloadAttributes(AtPositions([frameBytes, enablerBytes])));

        string? parsedId = parsed.RootElement.GetProperty("result").GetProperty("payloadId").GetString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsedId, Is.EqualTo(samePositions.Data.PayloadId), parsed.RootElement.ToString());
            Assert.That(parsedId, Is.Not.EqualTo(otherPositions.Data.PayloadId));
        }
    }

    [Test]
    public async Task Unknown_gas_dimensions_share_profile_2_replay_verdicts()
    {
        IneligibleReplayer replayer = new();
        using MergeTestBlockchain chain = await CreateProfile2Blockchain(
            new MergeConfig { TerminalTotalDifficulty = "0", NewPayloadCacheSize = 0 },
            builder => builder.AddSingleton<IProfile2EligibilityReplayer>(replayer));
        (GetPayloadV7Result built, byte[] frameTx, byte[] enabler, _) = await BuildProfile2Payload(chain);
        ExecutionPayloadV4 payload = built.ExecutionPayload;
        byte[][][] inclusionLists = AtPositions([frameTx, enabler]);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ResultWrapper<PayloadStatusV2> first = await rpc.engine_newPayloadV6(payload, [], Keccak.Zero, built.ExecutionRequests, inclusionLists, []);
        await InclusionVerdict(rpc, first.Data);
        await rpc.engine_forkchoiceUpdatedV5(new ForkchoiceStateV1(payload.BlockHash, payload.BlockHash, payload.BlockHash), null);
        chain.BlockTree.FindHeader(payload.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.GasUsedPerDimension = null;
        replayer.Batches = 0;

        // A claim at the end of the payload names another check of the same block, run without the gas dimensions.
        ResultWrapper<PayloadStatusV2> resent = await rpc.engine_newPayloadV6(payload, [], Keccak.Zero, built.ExecutionRequests, inclusionLists,
            [new OmissionClaim(Keccak.Compute(frameTx), (ulong)payload.Transactions.Length)]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await InclusionVerdict(rpc, resent.Data), Is.True);
            Assert.That(replayer.Batches, Is.EqualTo(1));
        }
    }

    private sealed class IneligibleReplayer : IProfile2EligibilityReplayer
    {
        public int Batches { get; set; }
        public bool AreSignaturesValid(Transaction transaction, IReleaseSpec spec) => true;
        public bool[] AreEligible(Block block, IReadOnlyList<(Transaction Transaction, int Index)> requests, IReleaseSpec spec)
        {
            Batches++;
            return new bool[requests.Count];
        }
    }

    private async Task<MergeTestBlockchain> CreateProfile2Blockchain(MergeConfig? config = null, Action<ContainerBuilder>? configure = null) =>
        await CreateBlockchain(
            Eip8369Prototype.Instance,
            config ?? new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder =>
            {
                configure?.Invoke(builder);
                builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((worldState, specProvider) =>
                    new FunctionalGenesisPostProcessor(_ =>
                    {
                        worldState.CreateAccount(Profile2Sender, 10.Ether);
                        worldState.InsertCode(Profile2Sender, ApproveOnceSlotIsSet, specProvider.GenesisSpec);
                        worldState.RecalculateStateRoot();
                    }));
            });

    /// <summary>Builds on the head with <c>[frame transaction, enabler]</c>, in that order, at committee position 0.</summary>
    private async Task<(GetPayloadV7Result Built, byte[] FrameTx, byte[] Enabler, string PayloadId)> BuildProfile2Payload(MergeTestBlockchain chain)
    {
        byte[] frameBytes = Encode(Profile2FrameTx(chain, Profile2Sender, 100_000));
        byte[] enablerBytes = Encode(Profile2Enabler());
        (GetPayloadV7Result built, string payloadId) = await BuildWithFcu(chain, BuildBogotaPayloadAttributes(AtPositions([frameBytes, enablerBytes])));
        return (built, frameBytes, enablerBytes, payloadId);
    }

    private static async Task<(GetPayloadV7Result Built, string PayloadId)> BuildWithFcu(MergeTestBlockchain chain, PayloadAttributes attributes)
    {
        Hash256 head = chain.BlockTree.HeadHash;
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(new ForkchoiceStateV1(head, Keccak.Zero, head), attributes);
        Assert.That(fcu.Data?.PayloadId, Is.Not.Null, fcu.Result.Error);
        ResultWrapper<GetPayloadV7Result?> built = await chain.EngineRpcModule.engine_getPayloadV7(Bytes.FromHexString(fcu.Data!.PayloadId!));
        return (built.Data!, fcu.Data.PayloadId!);
    }

    private static Transaction Profile2FrameTx(MergeTestBlockchain chain, Address sender, ulong verifyGas) => new()
    {
        Type = TxType.FrameTx,
        ChainId = chain.SpecProvider.ChainId,
        Nonce = 0,
        SenderAddress = sender,
        // EIP-8250 replaces the scalar nonce; the zero key alone is the account nonce.
        NonceKeys = [UInt256.Zero],
        Frames = [new TxFrame(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, target: null, verifyGas, UInt256.Zero, default)],
        FrameSignatures = [],
        GasPrice = 2.GWei,
        DecodedMaxFeePerGas = 10.GWei,
    };

    private static Transaction Profile2Enabler() => Build.A.Transaction
        .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei).WithGasLimit(1_000_000)
        .WithType(TxType.EIP1559).WithTo(Profile2Sender).WithData([1]).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

    private static byte[] Encode(Transaction tx) => TxDecoder.Instance.Encode(tx, RlpBehaviors.SkipTypedWrapping).Bytes;

    // Called with calldata it sets slot 0; as a VERIFY frame it approves only once slot 0 is nonzero.
    private static readonly byte[] ApproveOnceSlotIsSet =
    [
        (byte)Instruction.CALLDATASIZE, (byte)Instruction.PUSH1, 0x13, (byte)Instruction.JUMPI,
        (byte)Instruction.PUSH1, 0, (byte)Instruction.SLOAD, (byte)Instruction.ISZERO,
        (byte)Instruction.PUSH1, 0x1a, (byte)Instruction.JUMPI,
        (byte)Instruction.PUSH1, (byte)FrameFlags.ApproveExecutionAndPayment,
        (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 0, (byte)Instruction.APPROVE, (byte)Instruction.STOP,
        (byte)Instruction.JUMPDEST, (byte)Instruction.PUSH1, 1, (byte)Instruction.PUSH1, 0, (byte)Instruction.SSTORE, (byte)Instruction.STOP,
        (byte)Instruction.JUMPDEST, (byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 0, (byte)Instruction.REVERT,
    ];
}
