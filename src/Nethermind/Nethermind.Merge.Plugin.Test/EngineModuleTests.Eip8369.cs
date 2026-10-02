// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Consensus.Producers;
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
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

/// <summary>EIP-8369 claimed evaluation indices and per-includer lists over the engine API.</summary>
/// <remarks>The frame sender approves only once its slot 0 is set, which the list's second entry does, so the
/// builder's attempt at index 0 fails while the candidate is eligible at the end of the payload.</remarks>
public partial class EngineModuleTests
{
    private static readonly Address Profile2Sender = TestItem.AddressD;

    [TestCase(true, TestName = "Claim from getPayloadV7 excuses the omission through newPayloadV6 and forkchoiceUpdatedV5")]
    [TestCase(false, TestName = "Without the claim the omission is judged at the end of the payload")]
    public async Task Claimed_index_travels_from_getPayload_through_newPayloadV6_to_forkchoiceUpdated(bool sendClaims)
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        (GetPayloadV7Result built, byte[] frameTx, byte[] enabler, _) = await BuildProfile2Payload(chain);
        ExecutionPayloadV4 payload = built.ExecutionPayload;
        (byte[][] il, byte[][] membership) = Lists([frameTx, enabler]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.Transactions, Is.EqualTo(new[] { enabler }), "only the enabling entry was appended");
            Assert.That(built.InclusionListClaims, Is.EqualTo(new[] { new InclusionListClaim(Keccak.Compute(frameTx), 0) }));
        }

        ResultWrapper<PayloadStatusV2> newPayload = await rpc.engine_newPayloadV6(
            payload, [], Keccak.Zero, built.ExecutionRequests, il, membership, sendClaims ? built.InclusionListClaims : null);
        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(payload.BlockHash, payload.BlockHash, payload.BlockHash), payloadAttributes: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(newPayload.Result.ResultType, Is.EqualTo(ResultType.Success), newPayload.Result.Error);
            Assert.That(newPayload.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(newPayload.Data.InclusionListSatisfied, Is.EqualTo(sendClaims));
            Assert.That(fcu.Data.PayloadStatus.InclusionListSatisfied, Is.EqualTo(sendClaims));
            Assert.That(chain.Container.Resolve<IInclusionListComplianceEvaluator>().TryEvaluate(payload.BlockHash, il), Is.Null,
                "raw inclusion-list bytes cannot recover membership or claimed indices");
        }
    }

    /// <summary>newPayloadWithWitnessV6 carries membership and claims as newPayloadV6 does; its response has no
    /// compliance field, so the verdict is read off the forkchoiceUpdatedV5 that follows.</summary>
    [TestCase(true, TestName = "Claims sent through newPayloadWithWitnessV6 excuse the omission")]
    [TestCase(false, TestName = "Without claims newPayloadWithWitnessV6 judges the omission at the end of the payload")]
    public async Task NewPayloadWithWitnessV6_carries_membership_and_claims(bool sendClaims)
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        (GetPayloadV7Result built, byte[] frameTx, byte[] enabler, _) = await BuildProfile2Payload(chain);
        ExecutionPayloadV4 payload = built.ExecutionPayload;
        (byte[][] il, byte[][] membership) = Lists([frameTx, enabler]);

        ResultWrapper<NewPayloadWithWitnessV1Result> newPayload = await rpc.engine_newPayloadWithWitnessV6(
            payload, [], Keccak.Zero, built.ExecutionRequests, il, membership, sendClaims ? built.InclusionListClaims : null);
        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(payload.BlockHash, payload.BlockHash, payload.BlockHash), payloadAttributes: null);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(newPayload.Data.Status, Is.EqualTo(PayloadStatus.Valid), newPayload.Result.Error);
            Assert.That(fcu.Data.PayloadStatus.InclusionListSatisfied, Is.EqualTo(sendClaims));
        }
    }

    /// <summary>Claims change the verdict, so a result cached for the same block and list must not answer a
    /// request whose claims differ.</summary>
    [Test]
    public async Task NewPayloadV6_does_not_reuse_a_verdict_cached_under_other_claims()
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        (GetPayloadV7Result built, byte[] frameTx, byte[] enabler, _) = await BuildProfile2Payload(chain);
        (byte[][] il, byte[][] membership) = Lists([frameTx, enabler]);

        ResultWrapper<PayloadStatusV2> claimed = await rpc.engine_newPayloadV6(built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, il, membership, built.InclusionListClaims);
        // Canonical, so the resend is answered from the result cache or the committed state, not re-executed.
        Hash256 hash = built.ExecutionPayload.BlockHash;
        await rpc.engine_forkchoiceUpdatedV5(new ForkchoiceStateV1(hash, hash, hash), payloadAttributes: null);
        ResultWrapper<PayloadStatusV2> unclaimed = await rpc.engine_newPayloadV6(built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, il, membership);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(claimed.Data.InclusionListSatisfied, Is.True);
            Assert.That(unclaimed.Data.InclusionListSatisfied, Is.False);
        }
    }

    /// <summary>EIP-8369 § Includers over the wire: the stuffing and the candidate overrun one committee position's
    /// VERIFY budget and excuse the candidate, while at two positions each has its own budget and the candidate is judged.</summary>
    [TestCase(true, true, TestName = "Membership fills the VERIFY budget per committee position")]
    [TestCase(false, false, TestName = "Entries at separate committee positions draw on separate budgets")]
    public async Task NewPayloadV6_fills_the_verify_budget_per_position(bool samePosition, bool satisfied)
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        // Each declares 600,000 of VERIFY gas, so whichever the fill takes second overruns the position's 1,048,576;
        // the stuffing's sender is picked so that its hash, and so its turn, comes first.
        byte[] candidate = Encode(Profile2FrameTx(chain, Profile2Sender, 600_000));
        byte[] stuffing = TestItem.Addresses.Select(a => Encode(Profile2FrameTx(chain, a, 600_000)))
            .First(tx => Keccak.Compute(tx).CompareTo(Keccak.Compute(candidate)) < 0);
        byte[] enabler = Encode(Profile2Enabler());
        // One payload, built with all three at one position so the candidate is tried first and omitted.
        (byte[][] il, byte[][] built3) = Lists([stuffing, candidate, enabler]);
        (GetPayloadV7Result built, _) = await BuildWithFcu(chain, BuildBogotaPayloadAttributes((il, built3)));
        // Aligned with il = [stuffing, candidate, enabler]: the candidate alone at position 1.
        byte[][] membership = samePosition ? built3 : [Bitvector(1), Bitvector(2), Bitvector(1)];
        Assume.That(built.ExecutionPayload.Transactions, Is.EqualTo(new[] { enabler }));

        ResultWrapper<PayloadStatusV2> newPayload = await chain.EngineRpcModule.engine_newPayloadV6(
            built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, il, membership);

        Assert.That(newPayload.Data.InclusionListSatisfied, Is.EqualTo(satisfied), newPayload.Result.Error);
    }

    /// <summary>The claims round-trip the JSON wire: getPayloadV7 writes them, newPayloadV6 parses them back
    /// beside the membership.</summary>
    [Test]
    public async Task Claims_and_membership_round_trip_the_json_wire()
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        IEngineRpcModule rpc = chain.EngineRpcModule;
        (_, byte[] frameTx, byte[] enabler, string payloadId) = await BuildProfile2Payload(chain);

        using JsonDocument getPayload = JsonDocument.Parse(await RpcTest.TestSerializedRequest(rpc, "engine_getPayloadV7", payloadId));
        JsonElement result = getPayload.RootElement.GetProperty("result");
        JsonElement claims = result.GetProperty("inclusionListClaims");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(claims.GetArrayLength(), Is.EqualTo(1));
            Assert.That(claims[0].GetProperty("transactionHash").GetString(), Is.EqualTo(Keccak.Compute(frameTx).ToString()));
            Assert.That(claims[0].GetProperty("transactionIndex").GetString(), Is.EqualTo("0x0"));
        }

        using JsonDocument newPayload = JsonDocument.Parse(await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV6",
            result.GetProperty("executionPayload"), System.Array.Empty<string>(), Keccak.Zero, result.GetProperty("executionRequests"),
            new[] { frameTx.ToHexString(true), enabler.ToHexString(true) }, new[] { "0x0100", "0x0100" }, claims));
        Assert.That(newPayload.RootElement.GetProperty("result").GetProperty("inclusionListSatisfied").GetBoolean(), Is.True);
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
        PayloadAttributes attributes = BuildBogotaPayloadAttributes(inclusionList: []);
        if (!bogota) (attributes.InclusionListTransactions, attributes.InclusionListMembership) = (null, null);
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

    /// <summary>bogota.md engine_newPayloadV6 point 2 and its forkchoiceUpdatedV5 counterpart, each paired with
    /// an arm at the bound so a method rejecting everything cannot pass.</summary>
    public static IEnumerable<TestCaseData> MembershipBounds
    {
        get
        {
            static TestCaseData Case(string name, byte[][] il, byte[]?[] membership, bool invalid) =>
                new(il, membership, invalid) { TestName = name };
            byte[] one = [0x01];
            byte[] half = new byte[Eip7805Constants.MaxBytesPerInclusionList / 2 + 1];

            yield return Case("Membership shorter than the list is invalid", [one, one], [Bitvector(1)], true);
            yield return Case("Membership entry of one byte is invalid", [one], [[0x01]], true);
            yield return Case("Membership entry of three bytes is invalid", [one], [[0x01, 0x00, 0x00]], true);
            yield return Case("Membership entry with no bit set is invalid", [one], [Bitvector(0)], true);
            yield return Case("Membership entry with the last position set is valid", [one], [Bitvector(1 << 15)], false);
            yield return Case("Two halves over one position's byte cap are invalid", [half, half], [Bitvector(1), Bitvector(1)], true);
            yield return Case("Two halves at two positions are valid", [half, half], [Bitvector(1), Bitvector(2)], false);
        }
    }

    [TestCaseSource(nameof(MembershipBounds))]
    public async Task NewPayloadV6_bounds_the_membership(byte[][] il, byte[]?[] membership, bool invalid)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        (GetPayloadV7Result built, _) = await BuildWithFcu(chain, BuildBogotaPayloadAttributes(inclusionList: []));

        ResultWrapper<PayloadStatusV2> result = await chain.EngineRpcModule.engine_newPayloadV6(
            built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, il, membership!);

        Assert.That(result.ErrorCode, Is.EqualTo(invalid ? ErrorCodes.InvalidParams : ErrorCodes.None), result.Result.Error);
    }

    [TestCaseSource(nameof(MembershipBounds))]
    public async Task ForkchoiceUpdatedV5_bounds_the_membership(byte[][] il, byte[]?[] membership, bool invalid)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        Hash256 head = chain.BlockTree.HeadHash;
        PayloadAttributes attributes = BuildBogotaPayloadAttributes(inclusionList: il);
        attributes.InclusionListMembership = membership!;

        ResultWrapper<ForkchoiceUpdatedV2Result> result = await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(new ForkchoiceStateV1(head, Keccak.Zero, head), attributes);

        Assert.That(result.ErrorCode, Is.EqualTo(invalid ? MergeErrorCodes.InvalidPayloadAttributes : ErrorCodes.None), result.Result.Error);
    }

    /// <summary>bogota.md makes the membership positional wherever inclusion lists are, with or without EIP-8141.</summary>
    [TestCase(true, TestName = "Membership is required when EIP-8141 frames are active")]
    [TestCase(false, TestName = "Membership is required under EIP-7805 alone")]
    public async Task Membership_is_required_with_frame_transactions(bool frames)
    {
        using MergeTestBlockchain chain = frames ? await CreateProfile2Blockchain() : await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        Hash256 head = chain.BlockTree.HeadHash;
        (GetPayloadV7Result built, _) = await BuildWithFcu(chain, BuildBogotaPayloadAttributes(Lists()));
        byte[][] il = [Encode(Profile2Enabler())];

        ResultWrapper<PayloadStatusV2> newPayload = await chain.EngineRpcModule.engine_newPayloadV6(
            built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, il, inclusionListMembership: null);
        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(head, Keccak.Zero, head), WithoutMembership(BuildBogotaPayloadAttributes(inclusionList: il)));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(newPayload.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams), newPayload.Result.Error);
            Assert.That(fcu.ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidPayloadAttributes), fcu.Result.Error);
        }
    }

    [TestCase(Eip8369Constants.MaxInclusionListClaims + 1, true, TestName = "Claims over MAX_INCLUSION_LIST_CLAIMS are invalid params")]
    [TestCase(Eip8369Constants.MaxInclusionListClaims, false, TestName = "Claims at MAX_INCLUSION_LIST_CLAIMS are accepted")]
    public async Task NewPayloadV6_bounds_the_claims(int count, bool invalid)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        (GetPayloadV7Result built, _) = await BuildWithFcu(chain, BuildBogotaPayloadAttributes(inclusionList: []));

        ResultWrapper<PayloadStatusV2> result = await chain.EngineRpcModule.engine_newPayloadV6(
            built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, [], [], [.. Enumerable.Repeat(new InclusionListClaim(Keccak.Zero, 0), count)]);

        Assert.That(result.ErrorCode, Is.EqualTo(invalid ? ErrorCodes.InvalidParams : ErrorCodes.None), result.Result.Error);
    }

    /// <summary>The builder fills each committee position's VERIFY budget apart, in ascending hash order: the
    /// stuffed position admits two of its three entries and the third gets no claim, while the other position's
    /// candidate keeps its claim however the first spends.</summary>
    [Test]
    public async Task ForkchoiceUpdatedV5_builder_claims_per_committee_position()
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        // 520,000 each: two fit one position's 1,048,576, a third does not; pooled, the two would exhaust the budget.
        byte[][] stuffing = [.. new[] { TestItem.AddressE, TestItem.AddressF, TestItem.Addresses[0] }.Select(a => Encode(Profile2FrameTx(chain, a, 520_000)))];
        byte[] frameBytes = Encode(Profile2FrameTx(chain, Profile2Sender, 100_000));
        byte[] enablerBytes = Encode(Profile2Enabler());
        (byte[][] il, byte[][] membership) = Lists(stuffing, [frameBytes, enablerBytes]);
        Hash256[] admittedStuffing = [.. stuffing.Select(Keccak.Compute).Order().Take(2)];

        (GetPayloadV7Result built, _) = await BuildWithFcu(chain, BuildBogotaPayloadAttributes((il, membership)));
        ResultWrapper<PayloadStatusV2> newPayload = await chain.EngineRpcModule.engine_newPayloadV6(
            built.ExecutionPayload, [], Keccak.Zero, built.ExecutionRequests, il, membership, built.InclusionListClaims);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(built.ExecutionPayload.Transactions, Is.EqualTo(new[] { enablerBytes }));
            Assert.That(built.InclusionListClaims, Is.EquivalentTo(
                admittedStuffing.Select(h => new InclusionListClaim(h, 0)).Append(new InclusionListClaim(Keccak.Compute(frameBytes), 0))));
            Assert.That(newPayload.Data.InclusionListSatisfied, Is.True, newPayload.Result.Error);
        }
    }

    /// <summary>PayloadAttributesV5 parses the membership from the wire, and it is part of the build's identity:
    /// the same list under another membership is a different build, since membership changes the claims.</summary>
    [Test]
    public async Task ForkchoiceUpdatedV5_parses_membership_into_a_distinct_build()
    {
        using MergeTestBlockchain chain = await CreateProfile2Blockchain();
        byte[] frameBytes = Encode(Profile2FrameTx(chain, Profile2Sender, 100_000));
        byte[] enablerBytes = Encode(Profile2Enabler());
        Hash256 head = chain.BlockTree.HeadHash;
        PayloadAttributes flat = BuildBogotaPayloadAttributes(inclusionList: []);

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
                inclusionListTransactions = new[] { frameBytes.ToHexString(true), enablerBytes.ToHexString(true) },
                inclusionListMembership = new[] { "0x0100", "0x0200" },
            }));
        ResultWrapper<ForkchoiceUpdatedV2Result> membershipFcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(head, Keccak.Zero, head), BuildBogotaPayloadAttributes(Lists([frameBytes], [enablerBytes])));
        ResultWrapper<ForkchoiceUpdatedV2Result> otherMembershipFcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(head, Keccak.Zero, head), BuildBogotaPayloadAttributes(Lists([frameBytes, enablerBytes])));

        string? parsedId = parsed.RootElement.GetProperty("result").GetProperty("payloadId").GetString();
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parsedId, Is.EqualTo(membershipFcu.Data.PayloadId), parsed.RootElement.ToString());
            Assert.That(parsedId, Is.Not.EqualTo(otherMembershipFcu.Data.PayloadId));
        }
    }

    private async Task<MergeTestBlockchain> CreateProfile2Blockchain() =>
        await CreateBlockchain(
            new OverridableReleaseSpec(Bogota.Instance) { IsEip8141Enabled = true },
            new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((worldState, specProvider) =>
                new FunctionalGenesisPostProcessor(_ =>
                {
                    worldState.CreateAccount(Profile2Sender, 10.Ether);
                    worldState.InsertCode(Profile2Sender, ApproveOnceSlotIsSet, specProvider.GenesisSpec);
                    worldState.RecalculateStateRoot();
                })));

    /// <summary>Builds on the head with the flat list <c>[frame transaction, enabler]</c>, in that order.</summary>
    private async Task<(GetPayloadV7Result Built, byte[] FrameTx, byte[] Enabler, string PayloadId)> BuildProfile2Payload(MergeTestBlockchain chain)
    {
        byte[] frameBytes = Encode(Profile2FrameTx(chain, Profile2Sender, 100_000));
        byte[] enablerBytes = Encode(Profile2Enabler());
        (GetPayloadV7Result built, string payloadId) = await BuildWithFcu(chain, BuildBogotaPayloadAttributes(Lists([frameBytes, enablerBytes])));
        return (built, frameBytes, enablerBytes, payloadId);
    }

    private PayloadAttributes BuildBogotaPayloadAttributes((byte[][] Transactions, byte[][] Membership) inclusionList)
    {
        PayloadAttributes attributes = BuildBogotaPayloadAttributes(inclusionList: inclusionList.Transactions);
        attributes.InclusionListMembership = inclusionList.Membership;
        return attributes;
    }

    private static PayloadAttributes WithoutMembership(PayloadAttributes attributes)
    {
        attributes.InclusionListMembership = null;
        return attributes;
    }

    /// <summary>Deduplicates committee members' lists into the engine API's list and its membership bitvectors.</summary>
    private static (byte[][] Transactions, byte[][] Membership) Lists(params byte[][][] lists)
    {
        List<byte[]> transactions = [];
        List<ushort> masks = [];
        for (int position = 0; position < lists.Length; position++)
        {
            foreach (byte[] tx in lists[position])
            {
                int index = transactions.FindIndex(t => Bytes.AreEqual(t, tx));
                if (index < 0)
                {
                    transactions.Add(tx);
                    masks.Add(0);
                    index = transactions.Count - 1;
                }
                masks[index] |= (ushort)(1 << position);
            }
        }
        return ([.. transactions], [.. masks.Select(Bitvector)]);
    }

    private static byte[] Bitvector(ushort mask) => [(byte)mask, (byte)(mask >> 8)];

    private static async Task<(GetPayloadV7Result Built, string PayloadId)> BuildWithFcu(MergeTestBlockchain chain, PayloadAttributes attributes)
    {
        Hash256 head = chain.BlockTree.HeadHash;
        ResultWrapper<ForkchoiceUpdatedV2Result> fcu = await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(new ForkchoiceStateV1(head, Keccak.Zero, head), attributes);
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
