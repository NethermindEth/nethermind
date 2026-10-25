// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Blockchain;
using Nethermind.Blockchain.BlockAccessLists;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus;
using Nethermind.Consensus.Decoders;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
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
using Nethermind.Logging;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.Handlers;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Nethermind.State;
using Nethermind.Trie;
using Nethermind.TxPool;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public void Inclusion_list_bal_view_skips_state_for_full_or_included_blocks([Values] bool fullBlock)
    {
        Transaction entry = BuildInclusionListTransfer();
        Block block = Build.A.Block.WithGasLimit(30_000_000).WithGasUsed(fullBlock ? 30_000_000UL : 0)
            .WithTransactions(fullBlock ? [] : [entry]).TestObject;
        IReadOnlyStateProvider committed = Substitute.For<IReadOnlyStateProvider>();
        IReadOnlyStateProvider parent = Substitute.For<IReadOnlyStateProvider>();
        InclusionListCommittedState state = new(committed, (uint)block.Transactions.Length + 1,
            () => throw new AssertionException("The validator must not decode an unused BAL."), () => parent);

        bool satisfied = InclusionListValidator.IsSatisfied(block, [[entry]], state, Bogota.Instance, Substitute.For<ITxValidator>(), 0, null);

        Assert.That(satisfied, Is.True);
        committed.DidNotReceive().TryGetAccount(Arg.Any<Address>(), out Arg.Any<AccountStruct>());
        parent.DidNotReceive().TryGetAccount(Arg.Any<Address>(), out Arg.Any<AccountStruct>());
    }

    [Test]
    public void Inclusion_list_committed_state_uses_latest_bal_values([Values(0U, 1U)] uint priorIndex)
    {
        byte[] delegation = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressA.Bytes];
        AccountStruct finalAccount = new(8, 2.Ether, Keccak.EmptyTreeHash, ValueKeccak.Compute([0x00]));
        IReadOnlyStateProvider committed = Substitute.For<IReadOnlyStateProvider>();
        committed.TryGetAccount(TestItem.AddressB, out Arg.Any<AccountStruct>()).Returns(call => { call[1] = finalAccount; return true; });
        ValueHash256 oldCodeHash = ValueKeccak.Compute(delegation);
        ReadOnlyAccountChanges changes = new(TestItem.AddressB, [], [],
            [new(priorIndex, 1.Ether), new(2, 2.Ether)], [new(priorIndex, 7), new(2, 8)],
            [new(priorIndex, delegation), new(2, [0x00])]);
        InclusionListCommittedState state = new(committed, 2, () => new([changes], 1),
            () => throw new AssertionException("A complete BAL prefix must not require parent state."));

        state.TryGetAccount(TestItem.AddressB, out AccountStruct account);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(account.Balance, Is.EqualTo(1.Ether));
            Assert.That(account.Nonce, Is.EqualTo(7));
            Assert.That(account.CodeHash, Is.EqualTo(oldCodeHash));
            Assert.That(((IReadOnlyStateProvider)state).IsDelegatedCode(TestItem.AddressB), Is.True);
            Assert.That(state.Available, Is.True);
        }
    }

    [Test]
    public void Inclusion_list_committed_state_restores_storage([Values] bool priorChange, [Values] bool finalizationChanged)
    {
        StorageCell cell = new(TestItem.AddressB, UInt256.One);
        IReadOnlyStateProvider committed = Substitute.For<IReadOnlyStateProvider>();
        committed.When(state => state.Get(in cell, out Arg.Any<UInt256>())).Do(call => call[1] = (UInt256)5);
        IReadOnlyStateProvider parent = Substitute.For<IReadOnlyStateProvider>();
        parent.When(state => state.Get(in cell, out Arg.Any<UInt256>())).Do(call => call[1] = (UInt256)3);
        StorageChange[] changes = finalizationChanged
            ? priorChange ? [new(1, 4), new(2, 5)] : [new(2, 5)]
            : [new(1, 5)];
        ReadOnlyAccountChanges account = new(TestItem.AddressB, [new(cell.Index, changes)], [], [], [], []);
        InclusionListCommittedState state = new(committed, 2, () => new([account], 1), () => parent);

        state.Get(in cell, out UInt256 value);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(value, Is.EqualTo((UInt256)(finalizationChanged ? priorChange ? 4 : 3 : 5)));
            Assert.That(state.Available, Is.True);
        }
    }

    [Test]
    public void Inclusion_list_committed_state_reads_addresses_outside_the_bal()
    {
        AccountStruct expected = new(0, 1.Ether);
        IReadOnlyStateProvider committed = Substitute.For<IReadOnlyStateProvider>();
        committed.TryGetAccount(TestItem.AddressB, out Arg.Any<AccountStruct>()).Returns(call => { call[1] = expected; return true; });
        InclusionListCommittedState state = new(committed, 2, () => new(), () => null);

        bool found = state.TryGetAccount(TestItem.AddressB, out AccountStruct account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.True);
            Assert.That(account, Is.EqualTo(expected));
            Assert.That(state.Available, Is.True);
        }
    }

    [Test]
    public void Inclusion_list_committed_state_preserves_empty_account_existence([Values] bool existed, [Values] bool finalizationCreatedBalance)
    {
        IReadOnlyStateProvider committed = Substitute.For<IReadOnlyStateProvider>();
        AccountStruct finalAccount = finalizationCreatedBalance ? new(1.Ether) : AccountStruct.TotallyEmpty;
        committed.TryGetAccount(TestItem.AddressB, out Arg.Any<AccountStruct>())
            .Returns(call => { call[1] = finalAccount; return finalizationCreatedBalance || existed; });
        IReadOnlyStateProvider parent = Substitute.For<IReadOnlyStateProvider>();
        parent.TryGetAccount(TestItem.AddressB, out Arg.Any<AccountStruct>())
            .Returns(call => { call[1] = AccountStruct.TotallyEmpty; return existed; });
        ReadOnlyAccountChanges changes = new(TestItem.AddressB, [], [], finalizationCreatedBalance ? [new(2, 1.Ether)] : [], [], []);
        InclusionListCommittedState state = new(committed, 2, () => new([changes], 1), () => parent);

        bool found = state.TryGetAccount(TestItem.AddressB, out AccountStruct account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(found, Is.EqualTo(existed));
            Assert.That(state.AccountExists(TestItem.AddressB), Is.EqualTo(existed), "cached existence");
            Assert.That(account.IsEmpty, Is.True);
            Assert.That(state.Available, Is.True);
        }
    }

    [Test]
    public void Inclusion_list_committed_state_serves_empty_code_without_reading_state()
    {
        InclusionListCommittedState state = new(Substitute.For<IReadOnlyStateProvider>(), 2,
            () => throw new AssertionException("Empty code needs no BAL."), () => null);

        ReadOnlyMemory<byte> code = state.GetCode(Keccak.OfAnEmptyString.ValueHash256);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(code.IsEmpty, Is.True);
            Assert.That(state.Available, Is.True);
        }
    }

    [Test]
    public void Inclusion_list_committed_state_marks_missing_code_unavailable([Values] bool codeAvailable)
    {
        byte[] expected = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressA.Bytes];
        ValueHash256 hash = ValueKeccak.Compute(expected);
        IReadOnlyStateProvider committed = Substitute.For<IReadOnlyStateProvider>();
        committed.GetCode(in hash).Returns(codeAvailable ? new ReadOnlyMemory<byte>(expected) : default);
        InclusionListCommittedState state = new(committed, 2, () => new(), () => null);

        ReadOnlyMemory<byte> code = state.GetCode(in hash);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(code.ToArray(), Is.EqualTo(codeAvailable ? expected : Array.Empty<byte>()));
            Assert.That(state.Available, Is.EqualTo(codeAvailable));
        }
    }

    [Test]
    public void Inclusion_list_committed_state_keeps_accounts_deleted_before_finalization_absent()
    {
        IReadOnlyStateProvider committed = Substitute.For<IReadOnlyStateProvider>();
        AccountStruct finalAccount = new(1.Ether);
        committed.TryGetAccount(TestItem.AddressB, out Arg.Any<AccountStruct>()).Returns(call => { call[1] = finalAccount; return true; });
        ReadOnlyAccountChanges changes = new(TestItem.AddressB, [], [], [new(1, 0), new(2, 1.Ether)], [], []);
        InclusionListCommittedState state = new(committed, 2, () => new([changes], 1),
            () => throw new AssertionException("The prefix already records the account deletion."));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.AccountExists(TestItem.AddressB), Is.False);
            Assert.That(state.Available, Is.True);
        }
    }

    [Test]
    public void Inclusion_list_committed_state_requires_parent_only_for_missing_prefix([Values] bool parentAvailable, [Values] bool finalizationChanged)
    {
        IReadOnlyStateProvider committed = Substitute.For<IReadOnlyStateProvider>();
        AccountStruct finalAccount = new(0, 2.Ether);
        committed.TryGetAccount(TestItem.AddressB, out Arg.Any<AccountStruct>()).Returns(call => { call[1] = finalAccount; return true; });
        IReadOnlyStateProvider parent = Substitute.For<IReadOnlyStateProvider>();
        AccountStruct parentAccount = new(0, 1.Ether);
        parent.TryGetAccount(TestItem.AddressB, out Arg.Any<AccountStruct>()).Returns(call => { call[1] = parentAccount; return true; });
        ReadOnlyAccountChanges changes = new(TestItem.AddressB, [], [], finalizationChanged ? [new(2, 2.Ether)] : [], [], []);
        InclusionListCommittedState state = new(committed, 2, () => new([changes], 1), () => parentAvailable ? parent : null);

        state.TryGetAccount(TestItem.AddressB, out AccountStruct account);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(state.Available, Is.EqualTo(parentAvailable || !finalizationChanged));
            if (state.Available) Assert.That(account.Balance, Is.EqualTo(finalizationChanged ? 1.Ether : 2.Ether));
        }
    }

    [Test]
    public async Task Inclusion_list_recheck_needs_a_valid_bal_only_for_account_checks([Values] UnavailableBal reason, [Values] bool omittedEntry)
    {
        IBlockAccessListStore unavailable = Substitute.For<IBlockAccessListStore>();
        if (reason == UnavailableBal.Corrupt) unavailable.Get(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(_ => throw new RlpException("Malformed BAL"));
        if (reason == UnavailableBal.Mismatched) unavailable.Get(Arg.Any<ulong>(), Arg.Any<Hash256>()).Returns(new ReadOnlyBlockAccessList([], 0, Keccak.Zero));
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.UpdateSingleton<NewPayloadHandler>(inner => inner.AddSingleton(unavailable)));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayloadV4 payload = await BuildAndInsertEmptyBlock(rpc, chain.BlockTree.HeadHash, 2);
        byte[][] list = omittedEntry ? [Rlp.Encode(BuildInclusionListTransfer()).Bytes] : [];

        bool? result = await Recheck(chain, payload, list, reason == UnavailableBal.CarriedMismatch
            ? new ReadOnlyBlockAccessList([], 0, Keccak.Zero) : null);

        Assert.That(result, Is.EqualTo(omittedEntry ? (bool?)null : true));
    }

    public enum UnavailableBal { Missing, Corrupt, Mismatched, CarriedMismatch }

    [Test]
    public async Task NewPayloadV6_ignores_blob_entries_without_hiding_ordinary_omissions([Values] bool ordinaryOmitted)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash;
        ResultWrapper<GetPayloadV7Result?> built = await BuildBogotaPayload(rpc, parent, BuildBogotaPayloadAttributes(inclusionList: []));
        ExecutionPayloadV4 payload = built.Data!.ExecutionPayload;
        Assert.That(payload.Transactions, Is.Empty);
        Transaction blob = Build.A.Transaction.WithType(TxType.Blob).WithNonce(0).WithGasLimit(100_000)
            .WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei).WithMaxFeePerBlobGas(1.GWei)
            .WithBlobVersionedHashes(1).WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        byte[][] list = ordinaryOmitted
            ? [Rlp.Encode(blob).Bytes, Rlp.Encode(BuildInclusionListTransfer()).Bytes] : [Rlp.Encode(blob).Bytes];

        ResultWrapper<PayloadStatusV2> result = await NewPayloadV6(rpc, payload, built.Data.ExecutionRequests, list);
        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(payload.BlockHash, payload.BlockHash, payload.BlockHash), payloadAttributes: null);
        bool? rechecked = await Recheck(chain, payload, list);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(await InclusionVerdict(rpc, result.Data), Is.EqualTo(!ordinaryOmitted));
            Assert.That(rechecked, Is.EqualTo(!ordinaryOmitted));
        }
    }

    [Test]
    public async Task NewPayloadV6_rechecks_changed_list_before_finalization([Values] FinalizationChange change)
    {
        Address sender = TestItem.PrivateKeyD.Address;
        byte[] ordinaryCode = [0x00];
        byte[] delegation = [.. Eip7702Constants.DelegationHeader, .. TestItem.AddressA.Bytes];
        bool funded = change is not (FinalizationChange.Funding or FinalizationChange.Withdrawal);
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance,
            new MergeConfig { TerminalTotalDifficulty = "0" }, configurer: builder => builder
                .WithGenesisPostProcessor((_, state) =>
                {
                    state.CreateAccount(sender, funded ? 1.Ether : 0);
                    byte[] code = change == FinalizationChange.Code ? ordinaryCode
                        : change == FinalizationChange.Delegation ? delegation : [];
                    if (code.Length > 0) state.InsertCode(sender, ValueKeccak.Compute(code), code, Bogota.Instance);
                })
                .AddDecorator<IExecutionRequestsProcessor>((context, inner) => new FinalizationAccountChange(inner, (state, spec) =>
                {
                    switch (change)
                    {
                        case FinalizationChange.Funding:
                            state.AddToBalanceAndCreateIfNotExists(sender, 1.Ether, spec);
                            break;
                        case FinalizationChange.Debit:
                            state.SubtractFromBalance(sender, state.GetBalance(sender), spec, out _);
                            break;
                        case FinalizationChange.Nonce:
                            state.SetNonce(sender, 1);
                            break;
                        case FinalizationChange.Code:
                        case FinalizationChange.Delegation:
                            byte[] code = change == FinalizationChange.Code ? [] : ordinaryCode;
                            state.InsertCode(sender, ValueKeccak.Compute(code), code, spec);
                            break;
                    }
                })));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash;
        PayloadAttributes attributes = BuildBogotaPayloadAttributes(inclusionList: []);
        if (change == FinalizationChange.Withdrawal)
            attributes.Withdrawals = [Build.A.Withdrawal.WithRecipient(sender).WithAmount(1_000_000_000).TestObject];
        ResultWrapper<GetPayloadV7Result?> built = await BuildBogotaPayload(rpc, parent, attributes);
        ExecutionPayloadV4 payload = built.Data!.ExecutionPayload;
        ResultWrapper<PayloadStatusV2> initial = await NewPayloadV6(rpc, payload, built.Data.ExecutionRequests, []);
        Assert.That(initial.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        await rpc.engine_forkchoiceUpdatedV5(new(payload.BlockHash, payload.BlockHash, payload.BlockHash), null);
        Transaction omitted = Build.A.Transaction.WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithGasLimit(100_000).WithValue(0).WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyD).TestObject;
        byte[][] inclusionList = [Rlp.Encode(omitted).Bytes];

        ResultWrapper<PayloadStatusV2> resend = await NewPayloadV6(rpc, payload, built.Data.ExecutionRequests, inclusionList);
        bool? storedAnswer = await Recheck(chain, payload, inclusionList);
        bool expected = change is FinalizationChange.Funding or FinalizationChange.Withdrawal or FinalizationChange.Code;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resend.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(await InclusionVerdict(rpc, resend.Data), Is.EqualTo(expected));
            Assert.That(storedAnswer, Is.EqualTo(expected));
        }
    }

    public enum FinalizationChange { Funding, Debit, Nonce, Code, Delegation, Withdrawal }

    private sealed class FinalizationAccountChange(IExecutionRequestsProcessor inner, Action<IWorldState, IReleaseSpec> change) : IExecutionRequestsProcessor
    {
        public void ProcessExecutionRequests(Block block, IWorldState state, TxReceipt[] receipts, IReleaseSpec spec)
        {
            inner.ProcessExecutionRequests(block, state, receipts, spec);
            if (block.Number > 0) change(state, spec);
        }
    }

    [Test]
    public async Task Should_process_block_as_expected_V7()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        PayloadAttributes payloadAttrs = BuildBogotaPayloadAttributes(inclusionList: []);
        ForkchoiceStateV1 fcuState = new(startingHead, Keccak.Zero, startingHead);

        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult = await rpc.engine_forkchoiceUpdatedV5(fcuState, payloadAttrs);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(fcuResult.Result.ResultType, Is.EqualTo(ResultType.Success), fcuResult.Result.Error);
            Assert.That(fcuResult.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(fcuResult.Data.PayloadId, Is.Not.Null);
        }

        ResultWrapper<GetPayloadV7Result?> payloadResult = await rpc.engine_getPayloadV7(Bytes.FromHexString(fcuResult.Data.PayloadId!));
        Assert.That(payloadResult.Data, Is.Not.Null);
        ExecutionPayloadV4 executionPayload = payloadResult.Data!.ExecutionPayload;
        Assert.That(executionPayload.Transactions, Is.Empty);

        ResultWrapper<PayloadStatusV2> newPayload = await NewPayloadV6(rpc, executionPayload, payloadResult.Data!.ExecutionRequests, []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(newPayload.Result.ResultType, Is.EqualTo(ResultType.Success), newPayload.Result.Error);
            Assert.That(newPayload.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(newPayload.Data.LatestValidHash, Is.EqualTo(executionPayload.BlockHash));
            Assert.That(newPayload.Data.InclusionCheckId,
                Is.EqualTo(InclusionCheckId.Compute(executionPayload.BlockHash, AtPositions(), [])));
            Assert.That(await InclusionVerdict(rpc, newPayload.Data), Is.True);
        }

        // VALID comes before the commit, which the forkchoice waits for only a bounded time before answering SYNCING.
        await chain.WaitForCommitted(executionPayload.BlockHash);
        ResultWrapper<ForkchoiceUpdatedV1Result> finalFcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(executionPayload.BlockHash, executionPayload.BlockHash, executionPayload.BlockHash),
            payloadAttributes: null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(finalFcu.Result.ResultType, Is.EqualTo(ResultType.Success), finalFcu.Result.Error);
            Assert.That(finalFcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(finalFcu.Data.PayloadStatus.LatestValidHash, Is.EqualTo(executionPayload.BlockHash));
            Assert.That(finalFcu.Data.PayloadId, Is.Null);
        }
    }

    [Test]
    public async Task NewPayloadV6_accepts_empty_hex_logs_bloom_of_log_less_block([Values] bool eip7668)
    {
        IReleaseSpec spec = eip7668 ? new OverridableReleaseSpec(Bogota.Instance) { IsEip7668Enabled = true } : Bogota.Instance;
        using MergeTestBlockchain chain = await CreateBlockchain(spec, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;
        ResultWrapper<ForkchoiceUpdatedV1Result> fcuResult = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead), BuildBogotaPayloadAttributes(inclusionList: []));
        ResultWrapper<GetPayloadV7Result?> payloadResult = await rpc.engine_getPayloadV7(Bytes.FromHexString(fcuResult.Data.PayloadId!));

        // Before EIP-7668 "0x" reads as the zero bloom, so the hash computed over 256 zero bytes still matches.
        JsonNode payloadJson = JsonNode.Parse(chain.JsonSerializer.Serialize(payloadResult.Data!.ExecutionPayload))!;
        payloadJson["logsBloom"] = "0x";
        ExecutionPayloadV4 executionPayload = chain.JsonSerializer.Deserialize<ExecutionPayloadV4>(payloadJson.ToJsonString())!;

        ResultWrapper<PayloadStatusV2> newPayload = await NewPayloadV6(rpc, executionPayload, payloadResult.Data!.ExecutionRequests, []);

        Assert.That(newPayload.Data.Status, Is.EqualTo(PayloadStatus.Valid), newPayload.Data.ValidationError);
    }

    [Test]
    public async Task NewPayloadV6_should_report_unsatisfied_inclusion_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayloadV4 emptyPayload = await BuildEmptyPayload(rpc, chain.BlockTree.HeadHash);

        // A transfer that fits the empty payload, so its omission is censorship.
        ResultWrapper<PayloadStatusV2> result = await NewPayloadV6(rpc, emptyPayload, [], [Rlp.Encode(BuildInclusionListTransfer()).Bytes]);

        // EIP-7805: a censoring payload stays VALID, and its inclusion check completes unsatisfied.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(result.Data.LatestValidHash, Is.EqualTo(emptyPayload.BlockHash));
            Assert.That(await InclusionVerdict(rpc, result.Data), Is.False);
        }
    }

    /// <summary>bogota.md § Deferred inclusion checking: one payload under other inputs is another check, each
    /// answering for its own inputs, and a completed verdict never changes.</summary>
    [Test]
    public async Task NewPayloadV6_checks_the_same_block_against_each_call_s_inclusion_lists([Values] bool censoringListLast)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayloadV4 emptyPayload = await BuildEmptyPayload(rpc, chain.BlockTree.HeadHash);
        byte[][] censoringList = [Rlp.Encode(BuildInclusionListTransfer()).Bytes];

        ResultWrapper<PayloadStatusV2> first = await NewPayloadV6(rpc, emptyPayload, [], censoringListLast ? [] : censoringList);
        bool? firstVerdict = await InclusionVerdict(rpc, first.Data);
        ResultWrapper<PayloadStatusV2> second = await NewPayloadV6(rpc, emptyPayload, [], censoringListLast ? censoringList : []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(second.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(second.Data.InclusionCheckId, Is.Not.EqualTo(first.Data.InclusionCheckId));
            Assert.That(firstVerdict, Is.EqualTo(censoringListLast));
            Assert.That(await InclusionVerdict(rpc, second.Data), Is.EqualTo(!censoringListLast));
            Assert.That((await rpc.engine_getInclusionListVerdictV1(first.Data.InclusionCheckId!)).Data, Is.EqualTo(censoringListLast),
                "a completed verdict does not change");
        }
    }

    /// <summary>A resend with the same inputs names the same check, which is already complete, so the verdict comes
    /// back with execution validity and no work is repeated.</summary>
    [Test]
    public async Task NewPayloadV6_answers_a_resent_check_from_its_completed_verdict()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayloadV4 emptyPayload = await BuildEmptyPayload(rpc, chain.BlockTree.HeadHash);
        byte[][] censoringList = [Rlp.Encode(BuildInclusionListTransfer()).Bytes];

        ResultWrapper<PayloadStatusV2> first = await NewPayloadV6(rpc, emptyPayload, [], censoringList);
        Assert.That(await InclusionVerdict(rpc, first.Data), Is.False);
        ResultWrapper<PayloadStatusV2> resend = await NewPayloadV6(rpc, emptyPayload, [], censoringList);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resend.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(resend.Data.InclusionCheckId, Is.EqualTo(first.Data.InclusionCheckId));
            Assert.That(resend.Data.InclusionListSatisfied, Is.False);
        }
    }

    /// <summary>An identifier this node never issued, or one it forgot, is unknown rather than pending, and polling
    /// it starts no check.</summary>
    [Test]
    public async Task GetInclusionListVerdictV1_answers_unknown_inclusion_check()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });

        ResultWrapper<bool?> first = await chain.EngineRpcModule.engine_getInclusionListVerdictV1(TestItem.KeccakA);
        ResultWrapper<bool?> again = await chain.EngineRpcModule.engine_getInclusionListVerdictV1(TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.ErrorCode, Is.EqualTo(MergeErrorCodes.UnknownInclusionCheck));
            Assert.That(first.Result.Error, Is.EqualTo("Unknown inclusion check"));
            Assert.That(again.ErrorCode, Is.EqualTo(MergeErrorCodes.UnknownInclusionCheck), "polling starts no check");
        }
    }

    /// <summary>A check whose block state is unavailable is forgotten rather than completed, so it is never taken as
    /// satisfied; resubmitting the original inputs once the state is back runs it again.</summary>
    [Test]
    public async Task Undecidable_inclusion_check_is_forgotten_until_the_inputs_are_resubmitted()
    {
        HeadStateInterceptor headState = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithHeadState(headState);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ExecutionPayloadV4 payload = await BuildEmptyPayload(rpc, chain.BlockTree.HeadHash);
        byte[][] censoringList = [Rlp.Encode(BuildInclusionListTransfer()).Bytes];

        headState.PrunedBlock = payload.BlockHash;
        ResultWrapper<PayloadStatusV2> unavailable = await NewPayloadV6(rpc, payload, [], censoringList);
        bool? forgotten = await InclusionVerdict(rpc, unavailable.Data);
        headState.PrunedBlock = null;
        ResultWrapper<PayloadStatusV2> resubmitted = await NewPayloadV6(rpc, payload, [], censoringList);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(unavailable.Data.Status, Is.EqualTo(PayloadStatus.Valid), "inclusion checking never holds execution validity");
            Assert.That(forgotten, Is.Null);
            Assert.That(resubmitted.Data.InclusionCheckId, Is.EqualTo(unavailable.Data.InclusionCheckId));
            Assert.That(await InclusionVerdict(rpc, resubmitted.Data), Is.False);
        }
    }

    /// <summary>bogota.md engine_newPayloadV6 point 2.1: one list per committee position, each within its byte cap
    /// before deduplication, and no empty entry; point 2.2: at most MAX_OMISSION_CLAIMS claims. Each is paired with an
    /// arm at the bound, so a method rejecting everything cannot pass.</summary>
    public static IEnumerable<TestCaseData> InclusionListBounds
    {
        get
        {
            static TestCaseData Case(string name, byte[][][] inclusionLists, int claims, bool invalid) =>
                new(inclusionLists, claims, invalid) { TestName = name };
            byte[] full = new byte[Eip7805Constants.MaxBytesPerInclusionList];
            byte[] half = new byte[Eip7805Constants.MaxBytesPerInclusionList / 2];

            yield return Case("Every position's list at its byte cap is valid", [.. Enumerable.Repeat<byte[][]>([full], Eip7805Constants.InclusionListCommitteeSize)], 0, false);
            yield return Case("A list one byte over its cap is invalid", AtPositions([full], [[.. full, 0]]), 0, true);
            yield return Case("A duplicate counts toward the cap before deduplication", AtPositions([half, half, [0]]), 0, true);
            yield return Case("A duplicate within the cap is valid", AtPositions([half, half]), 0, false);
            yield return Case("Two-byte entries filling every position are valid",
                [.. Enumerable.Repeat<byte[][]>([.. Enumerable.Range(0, Eip7805Constants.MaxBytesPerInclusionList / 2).Select(i => new[] { (byte)(i >> 8), (byte)i })], Eip7805Constants.InclusionListCommitteeSize)], 0, false);
            yield return Case("Fifteen lists are invalid", [.. Enumerable.Repeat<byte[][]>([], Eip7805Constants.InclusionListCommitteeSize - 1)], 0, true);
            yield return Case("Seventeen lists are invalid", [.. Enumerable.Repeat<byte[][]>([], Eip7805Constants.InclusionListCommitteeSize + 1)], 0, true);
            yield return Case("An empty entry is invalid", AtPositions([Array.Empty<byte>()]), 0, true);
            yield return Case("Claims at MAX_OMISSION_CLAIMS are valid", AtPositions(), Eip8369Constants.MaxOmissionClaims, false);
            yield return Case("Claims over MAX_OMISSION_CLAIMS are invalid", AtPositions(), Eip8369Constants.MaxOmissionClaims + 1, true);
        }
    }

    [TestCaseSource(nameof(InclusionListBounds))]
    public async Task NewPayloadV6_bounds_inclusion_lists_and_claims(byte[][][] inclusionLists, int claims, bool invalid)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        ExecutionPayloadV4 payload = await BuildEmptyPayload(chain.EngineRpcModule, chain.BlockTree.HeadHash);

        ResultWrapper<PayloadStatusV2> result = await chain.EngineRpcModule.engine_newPayloadV6(
            payload, [], Keccak.Zero, [], inclusionLists, [.. Enumerable.Repeat(new OmissionClaim(Keccak.Zero, 0), claims)]);

        Assert.That(result.ErrorCode, Is.EqualTo(invalid ? ErrorCodes.InvalidParams : ErrorCodes.None), result.Result.Error);
    }

    [TestCaseSource(nameof(InclusionListBounds))]
    public async Task ForkchoiceUpdatedV5_bounds_inclusion_lists(byte[][][] inclusionLists, int claims, bool invalid)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        Hash256 head = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV1Result> result = await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(head, Keccak.Zero, head), BuildBogotaPayloadAttributes(inclusionLists));

        // Payload attributes carry no claims, so only the list arms apply.
        Assert.That(result.ErrorCode, Is.EqualTo(invalid && claims == 0 ? MergeErrorCodes.InvalidPayloadAttributes : ErrorCodes.None), result.Result.Error);
    }

    /// <summary>bogota.md engine_newPayloadV6 point 2.2: a claim must match <c>OmissionClaimV1</c> on the wire, its index a
    /// canonical <c>QUANTITY</c> (common.md), while an index above the payload's transaction count is well formed and
    /// only clamped by EIP-7805.</summary>
    [TestCase("""{"transactionHash":"0x0000000000000000000000000000000000000000000000000000000000000000","evaluationIndex":"0xffffffffffffffff"}""", false, TestName = "Claim at the largest uint64 index is well formed")]
    [TestCase("""{"transactionHash":"0x0000000000000000000000000000000000000000000000000000000000000000","evaluationIndex":"0x10000000000000000"}""", true, TestName = "Claim index overflowing uint64 is invalid params")]
    [TestCase("""{"transactionHash":"0x0000000000000000000000000000000000000000000000000000000000000000"}""", true, TestName = "Claim without an index is invalid params")]
    [TestCase("""{"evaluationIndex":"0x0"}""", true, TestName = "Claim without a hash is invalid params")]
    [TestCase("""{"transactionHash":"0x00","evaluationIndex":"0x0"}""", true, TestName = "Claim with a short hash is invalid params")]
    [TestCase("""{"transactionHash":"0x0000000000000000000000000000000000000000000000000000000000000000","evaluationIndex":"0x0"}""", false, TestName = "Claim at index zero is well formed")]
    [TestCase("""{"transactionHash":"0x0000000000000000000000000000000000000000000000000000000000000000","evaluationIndex":"0xAbC"}""", false, TestName = "Claim index in either hex case is well formed")]
    [TestCase("""{"transactionHash":"0x0000000000000000000000000000000000000000000000000000000000000000","evaluationIndex":"0x01"}""", true, TestName = "Claim index with a leading zero is invalid params")]
    [TestCase("""{"transactionHash":"0x0000000000000000000000000000000000000000000000000000000000000000","evaluationIndex":"0x00"}""", true, TestName = "Claim index zero with a leading zero is invalid params")]
    [TestCase("""{"transactionHash":"0x0000000000000000000000000000000000000000000000000000000000000000","evaluationIndex":"0x"}""", true, TestName = "Claim index without digits is invalid params")]
    [TestCase("""{"transactionHash":"0x0000000000000000000000000000000000000000000000000000000000000000","evaluationIndex":"1"}""", true, TestName = "Claim index without 0x is invalid params")]
    [TestCase("""{"transactionHash":"0x0000000000000000000000000000000000000000000000000000000000000000","evaluationIndex":1}""", true, TestName = "Claim index as a JSON number is invalid params")]
    public async Task NewPayloadV6_rejects_a_malformed_claim(string claim, bool invalid)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        ExecutionPayloadV4 payload = await BuildEmptyPayload(chain.EngineRpcModule, chain.BlockTree.HeadHash);

        string response = await RpcTest.TestSerializedRequest(chain.EngineRpcModule, nameof(IEngineRpcModule.engine_newPayloadV6),
            payload, Array.Empty<string>(), Keccak.Zero, Array.Empty<string>(), AtPositions(), JsonDocument.Parse($"[{claim}]").RootElement);

        Assert.That(response, invalid ? Does.Contain($"\"code\":{ErrorCodes.InvalidParams}") : Does.Contain("\"status\":\"VALID\""));
    }

    [Test]
    public async Task NewPayloadV5_is_unsupported_at_Bogota()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ResultWrapper<GetPayloadV7Result?> payloadResult = await BuildBogotaPayload(rpc, chain.BlockTree.HeadHash, BuildBogotaPayloadAttributes(inclusionList: []));

        // At and after Bogota, engine_newPayloadV5 must be rejected with -38005.
        ResultWrapper<PayloadStatusV1> result = await rpc.engine_newPayloadV5(
            payloadResult.Data!.ExecutionPayload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
        }
    }

    // The witness wrapper delegates to a newPayload version, so rejecting V5 would leave it without an
    // entry point unless it gains its own V6.
    [Test]
    public async Task NewPayloadWithWitnessV6_supersedes_V5_at_Bogota()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        ResultWrapper<GetPayloadV7Result?> payloadResult = await BuildBogotaPayload(rpc, chain.BlockTree.HeadHash, BuildBogotaPayloadAttributes(inclusionList: []));
        ExecutionPayloadV4 executionPayload = payloadResult.Data!.ExecutionPayload;
        byte[][]? executionRequests = payloadResult.Data!.ExecutionRequests;

        using ResultWrapper<NewPayloadWithWitnessV1Result> v5 = await rpc.engine_newPayloadWithWitnessV5(
            executionPayload, [], Keccak.Zero, executionRequests);
        using ResultWrapper<NewPayloadWithWitnessV1Result> v6 = await rpc.engine_newPayloadWithWitnessV6(
            executionPayload, [], Keccak.Zero, executionRequests, AtPositions(), []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(v5.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(v5.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
            Assert.That(v6.Result.ResultType, Is.EqualTo(ResultType.Success), v6.Result.Error);
            Assert.That(v6.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(v6.Data.LatestValidHash, Is.EqualTo(executionPayload.BlockHash));
        }
    }

    [Test]
    public async Task NewPayloadV6_is_unsupported_before_Bogota()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Block genesis = chain.BlockFinder.FindGenesisBlock()!;

        PayloadAttributes attrs = CreateAmsterdamPayloadAttributes(genesis.Header);
        ForkchoiceStateV1 fcuState = new(genesis.Hash!, genesis.Hash!, genesis.Hash!);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV4(fcuState, attrs);
        ResultWrapper<GetPayloadV6Result?> payloadResult = await rpc.engine_getPayloadV6(Bytes.FromHexString(fcu.Data.PayloadId!));

        // Before Bogota, engine_newPayloadV6 must be rejected with -38005, ahead of any parameter check.
        ResultWrapper<PayloadStatusV2> result = await rpc.engine_newPayloadV6(
            payloadResult.Data!.ExecutionPayload, [], Keccak.Zero, payloadResult.Data!.ExecutionRequests, [], []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
        }
    }

    // bogota.md: PayloadAttributesV5 appends inclusionLists unconditionally, so sixteen empty lists are how a
    // proposer says it has none — omitting the field leaves the attributes V4-shaped.
    [Test]
    public async Task ForkchoiceUpdatedV5_rejects_attributes_without_inclusion_lists()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        ResultWrapper<ForkchoiceUpdatedV1Result> missing = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: null!));
        ResultWrapper<ForkchoiceUpdatedV1Result> empty = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: []));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(missing.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(missing.ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidPayloadAttributes));
            Assert.That(empty.Result.ResultType, Is.EqualTo(ResultType.Success), empty.Result.Error);
            Assert.That(empty.Data.PayloadId, Is.Not.Null);
        }
    }

    [Test]
    public async Task Should_build_block_with_inclusion_list_transactions()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        Transaction tx = Build.A.Transaction
            .WithNonce(0)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;
        byte[] txBytes = Rlp.Encode(tx).Bytes;

        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead),
            BuildBogotaPayloadAttributes(inclusionList: [txBytes]));

        using (Assert.EnterMultipleScope())
        {
            Assert.That(fcu.Data.PayloadStatus.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(fcu.Data.PayloadId, Is.Not.Null);
        }

        // Even the producer's EmptyBlock fast path carries the IL, so the first getPayload is populated.
        ResultWrapper<GetPayloadV7Result?> payloadResult = await rpc.engine_getPayloadV7(Bytes.FromHexString(fcu.Data.PayloadId!));
        Assert.That(payloadResult.Data, Is.Not.Null);
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.Transactions, Has.Length.EqualTo(1));
            Assert.That(payload.Transactions[0], Is.EqualTo(txBytes));
            Assert.That(payloadResult.Data.OmissionClaims, Is.Empty);
        }

        // The block-as-built must round-trip through newPayloadV6 with the same lists.
        ResultWrapper<PayloadStatusV2> verify = await NewPayloadV6(rpc, payload, [], [txBytes]);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(verify.Result.ResultType, Is.EqualTo(ResultType.Success), verify.Result.Error);
            Assert.That(verify.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(await InclusionVerdict(rpc, verify.Data), Is.True);
        }
    }

    [Test]
    public async Task NewPayloadV6_resubmitting_canonical_block_requires_state([Values] bool statePruned)
    {
        ConcurrentDictionary<Hash256, byte> pruned = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithPrunableState(pruned, releaseSpec: Bogota.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        Transaction tx = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        byte[][] inclusionList = [Rlp.Encode(tx).Bytes];

        ResultWrapper<GetPayloadV7Result?> payloadResult = await BuildBogotaPayload(rpc, startingHead, BuildBogotaPayloadAttributes(inclusionList: inclusionList));
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;

        ResultWrapper<PayloadStatusV2> first = await NewPayloadV6(rpc, payload, payloadResult.Data!.ExecutionRequests, inclusionList);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(first.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(await InclusionVerdict(rpc, first.Data), Is.True);
        }

        // Promote to canonical head, then re-submit the same payload and lists.
        await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(payload.BlockHash, payload.BlockHash, payload.BlockHash), payloadAttributes: null);
        if (statePruned) pruned[payload.BlockHash] = 0;

        // Execution validity is only reused while the state that established it is available.
        ResultWrapper<PayloadStatusV2> resend = await NewPayloadV6(rpc, payload, payloadResult.Data!.ExecutionRequests, inclusionList);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resend.Data.Status, Is.EqualTo(statePruned ? PayloadStatus.Syncing : PayloadStatus.Valid));
            Assert.That(resend.Data.InclusionCheckId, statePruned ? Is.Null : Is.EqualTo(first.Data.InclusionCheckId),
                "an identifier is present exactly when execution is VALID");
            if (!statePruned) Assert.That(resend.Data.InclusionListSatisfied, Is.True);
        }
    }

    [Test]
    public async Task Should_build_block_including_reversed_nonce_inclusion_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;

        Transaction tx0 = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Transaction tx1 = Build.A.Transaction
            .WithNonce(1).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

        // Reversed order (nonce 1 before nonce 0), split across two positions: a one-pass producer would skip
        // nonce 1 forever.
        ResultWrapper<GetPayloadV7Result?> payloadResult = await BuildBogotaPayload(rpc, chain.BlockTree.HeadHash,
            BuildBogotaPayloadAttributes(AtPositions([Rlp.Encode(tx1).Bytes], [Rlp.Encode(tx0).Bytes])));
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;

        // Both IL txs must be produced, in ascending-nonce order.
        Assert.That(payload.Transactions, Has.Length.EqualTo(2));
        using (Assert.EnterMultipleScope())
        {
            Assert.That(payload.Transactions[0], Is.EqualTo(Rlp.Encode(tx0).Bytes));
            Assert.That(payload.Transactions[1], Is.EqualTo(Rlp.Encode(tx1).Bytes));
        }
    }

    public async Task Can_get_inclusion_list()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        Transaction tx1 = Build.A.Transaction
            .WithNonce(0)
            .WithMaxFeePerGas(10.GWei)
            .WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        Transaction tx2 = Build.A.Transaction
            .WithNonce(1)
            .WithMaxFeePerGas(15.GWei)
            .WithMaxPriorityFeePerGas(3.GWei)
            .WithTo(TestItem.AddressB)
            .SignedAndResolved(TestItem.PrivateKeyB)
            .TestObject;

        chain.TxPool.SubmitTx(tx1, TxHandlingOptions.PersistentBroadcast);
        chain.TxPool.SubmitTx(tx2, TxHandlingOptions.PersistentBroadcast);

        using InclusionListBytes inclusionList = (await rpc.engine_getInclusionListV1()).Data!;

        byte[] tx1Bytes = Rlp.Encode(tx1).Bytes;
        byte[] tx2Bytes = Rlp.Encode(tx2).Bytes;
        byte[][] inclusionListBytes = inclusionList.Select(b => b.AsSpan().ToArray()).ToArray();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(inclusionList, Is.Not.Null);
            Assert.That(inclusionList.Count, Is.EqualTo(2));
            Assert.That(inclusionListBytes, Does.Contain(tx1Bytes));
            Assert.That(inclusionListBytes, Does.Contain(tx2Bytes));
        }
    }

    // The consensus layer names the block the list is requested for, so the parameter has to reach the
    // handler: dispatched as a no-argument method the call answers -32602 and no list is ever built.
    [TestCase(false, TestName = "GetInclusionListV1_without_a_parent_block_hash_builds_on_the_head")]
    [TestCase(true, TestName = "GetInclusionListV1_accepts_the_parent_block_hash")]
    public async Task GetInclusionListV1_serves_both_request_shapes(bool withParentBlockHash)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance);
        object?[] parameters = withParentBlockHash ? [chain.BlockTree.HeadHash] : [];

        string response = await RpcTest.TestSerializedRequest(chain.EngineRpcModule,
            nameof(IEngineRpcModule.engine_getInclusionListV1), parameters);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(response, Does.Contain("\"result\""));
            Assert.That(response, Does.Not.Contain("\"error\""));
        }
    }

    // The zero hash names no block, so it must fall back to the head rather than being looked up.
    [Test]
    public async Task GetInclusionListV1_treats_the_zero_parent_block_hash_as_unspecified()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance);

        ResultWrapper<InclusionListBytes> result =
            await chain.EngineRpcModule.engine_getInclusionListV1(Hash256.Zero);
        result.Data?.Dispose();

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
    }

    // A list built on a block this node does not have could not be appended to it.
    [Test]
    public async Task GetInclusionListV1_rejects_an_unknown_parent_block_hash()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance);

        ResultWrapper<InclusionListBytes> result =
            await chain.EngineRpcModule.engine_getInclusionListV1(TestItem.KeccakA);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
        }
    }

    [Test]
    public async Task GetInclusionListV1_before_Bogota_is_unsupported_fork()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Amsterdam.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ResultWrapper<InclusionListBytes> result = await rpc.engine_getInclusionListV1();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
        }
    }

    // The fallback payload has to satisfy the inclusion list, but must not pay for a mempool selection:
    // it is produced synchronously on the engine thread while every other engine call waits.
    [Test]
    public async Task Empty_block_fallback_carries_the_inclusion_list_without_the_mempool()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 startingHead = chain.BlockTree.HeadHash;

        Transaction inclusionListTx = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        Transaction mempoolTx = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
        Assert.That(chain.TxPool.SubmitTx(mempoolTx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));

        PayloadAttributes payloadAttributes = BuildBogotaPayloadAttributes(inclusionList: [Rlp.Encode(inclusionListTx).Bytes]);
        await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(startingHead, Keccak.Zero, startingHead), payloadAttributes);

        Block? emptyBlock = await chain.BlockProducer!.BuildBlock(
            chain.BlockTree.Head!.Header, payloadAttributes: payloadAttributes, flags: IBlockProducer.Flags.PrepareEmptyBlock);

        Assert.That(emptyBlock!.Transactions.Select(t => t.Hash), Is.EqualTo(new[] { inclusionListTx.Hash }));
    }

    // A canonical block this node ran is VALID, and its inclusion check is judged against its own state; once that
    // state is pruned below finality execution validity still stands, but the check cannot be decided and is
    // forgotten. A block this node never ran is answered SYNCING rather than re-executed, and no check is scheduled.
    [TestCase(true, false, PayloadStatus.Valid)]
    [TestCase(true, true, PayloadStatus.Valid)]
    [TestCase(false, false, PayloadStatus.Syncing)]
    public async Task NewPayloadV6_checks_inclusion_behind_head_against_its_own_state(
        bool runHere, bool statePruned, string expected)
    {
        ConcurrentDictionary<Hash256, byte> pruned = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithPrunableState(pruned, releaseSpec: Bogota.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;

        ExecutionPayloadV4 first = await BuildAndInsertEmptyBlock(rpc, chain.BlockTree.HeadHash, slot: 2);
        ExecutionPayloadV4 second = await BuildAndInsertEmptyBlock(rpc, first.BlockHash, slot: 3);
        ulong bestKnown = chain.BlockTree.BestKnownNumber;
        // What the pre-pivot header backfill leaves behind: on the main chain, never run by this node.
        if (!runHere) chain.BlockTree.GetInfo(first.BlockNumber, first.BlockHash).Info!.WasProcessed = false;
        if (statePruned) pruned[first.BlockHash] = 0;

        int processed = 0;
        chain.BranchProcessor.BlockProcessing += (_, _) => Interlocked.Increment(ref processed);
        ResultWrapper<PayloadStatusV2> resend = await NewPayloadV6(rpc, first, [], [Rlp.Encode(BuildInclusionListTransfer()).Bytes]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(resend.Data.Status, Is.EqualTo(expected),
                runHere ? "a block this node ran is answered from the tree" : "a block this node never ran is not answered from a state root");
            if (expected == PayloadStatus.Valid) Assert.That(await InclusionVerdict(rpc, resend.Data), statePruned ? Is.Null : Is.False);
            else Assert.That(resend.Data.InclusionCheckId, Is.Null);
            Assert.That(processed, Is.Zero, "a block on the node's own chain is not re-executed");
            Assert.That(chain.BlockTree.BestKnownNumber, Is.EqualTo(bestKnown));
            foreach (ExecutionPayloadV4 block in new[] { first, second })
            {
                Assert.That(chain.BlockTree.FindBlock(block.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded), Is.Not.Null, $"block {block.BlockNumber}");
            }
        }
    }

    /// <summary>
    /// The inclusion counterpart of <c>newPayloadV1_answers_valid_for_a_head_resent_while_its_re_execution_commits</c>:
    /// the resent head waits for its commit, and its inclusion check is judged against the restored state.
    /// </summary>
    [Test]
    public async Task NewPayloadV6_checks_inclusion_for_a_head_resent_while_its_re_execution_commits()
    {
        ConcurrentDictionary<Hash256, byte> pruned = new();
        CommitWaitProbe probe = new();
        using MergeTestBlockchain chain = await CreateBlockchainWithPrunableState(pruned,
            builder => builder.AddDecorator<IBlockProcessingQueue>((_, inner) => new CommitWaitObservingQueue(inner, probe, typeof(NewPayloadHandler))),
            Bogota.Instance);
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 genesisHash = chain.BlockTree.HeadHash!;

        // Built and made head without finalizing it, so the head can move back below it.
        ResultWrapper<GetPayloadV7Result?> built = await BuildBogotaPayload(rpc, genesisHash,
            BuildBogotaPayloadAttributes(inclusionList: [], timestamp: Timestamper.UnixTime.Seconds + 2, slotNumber: 2), finalizedHash: Keccak.Zero);
        ExecutionPayloadV4 block = built.Data!.ExecutionPayload;
        byte[][] requests = built.Data!.ExecutionRequests!;
        Assert.That((await NewPayloadV6(rpc, block, requests, [])).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        await rpc.engine_forkchoiceUpdatedV5(new ForkchoiceStateV1(block.BlockHash, Keccak.Zero, Keccak.Zero), payloadAttributes: null);

        // The Hive shape: the head moves back, the block's state is pruned, and the block is sent again and re-executed.
        await rpc.engine_forkchoiceUpdatedV5(new ForkchoiceStateV1(genesisHash, Keccak.Zero, Keccak.Zero), payloadAttributes: null);
        pruned[block.BlockHash] = 0;

        TaskCompletionSource commit = HoldCommit(chain, block.BlockHash);

        Assert.That((await NewPayloadV6(rpc, block, requests, [])).Data.Status, Is.EqualTo(PayloadStatus.Valid));
        MoveHeadBeforeCommit(chain, block.BlockHash);

        // Release the commit once the re-send waits on it or answers.
        probe.Watch(block.BlockHash);
        Task<ResultWrapper<PayloadStatusV2>> resent = NewPayloadV6(rpc, block, requests, [Rlp.Encode(BuildInclusionListTransfer()).Bytes]);
        await Task.WhenAny(probe.WaitEntered.Task, resent, Task.Delay(TimeSpan.FromSeconds(20)));
        bool waitedForCommit = probe.WaitEntered.Task.IsCompleted;
        commit.SetResult();
        ResultWrapper<PayloadStatusV2> result = await resent;

        using (Assert.EnterMultipleScope())
        {
            Assert.That(waitedForCommit, Is.True, "the re-send must wait for the head's committing re-execution");
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid),
                "the head's own re-execution is committing, so its execution validity is known");
            Assert.That(await InclusionVerdict(rpc, result.Data), Is.False);
        }
    }

    // The check must judge the list on the dimensions execution recorded, not on the max(execution, state) the
    // header reduces them to. The two differ only above the EIP-7825 execution cap: past it a transaction reserves
    // the cap on the execution dimension but its whole gas on the state one, so an entry that fits both dimensions
    // reads as unappendable against the max, and real censorship goes unreported. After cache loss, bounds still
    // prove some verdicts; an ambiguous check is forgotten rather than guessed, as recovering its exact answer needs
    // safe re-execution.
    [TestCase(50, false, InclusionListEntry.Boundary, false, TestName = "NewPayloadV6_checks_a_resent_block_on_the_recorded_gas_dimensions")]
    [TestCase(1, false, InclusionListEntry.Boundary, false, TestName = "NewPayloadV6_checks_a_resent_block_on_the_recorded_gas_dimensions_after_the_payload_cache_evicts_it")]
    [TestCase(1, true, InclusionListEntry.Boundary, false, TestName = "NewPayloadV6_forgets_a_check_once_both_caches_lose_the_gas_dimensions")]
    [TestCase(1, true, InclusionListEntry.Included, false, TestName = "NewPayloadV6_checks_an_included_list_after_both_dimension_caches_are_lost")]
    [TestCase(1, true, InclusionListEntry.Boundary, true, TestName = "NewPayloadV6_forgets_a_check_of_an_omitted_list_for_the_head_after_both_dimension_caches_are_lost")]
    [TestCase(1, true, InclusionListEntry.Included, true, TestName = "NewPayloadV6_checks_an_included_list_for_the_head_after_both_dimension_caches_are_lost")]
    [TestCase(1, true, InclusionListEntry.Small, false, TestName = "NewPayloadV6_reports_censorship_proven_without_gas_dimensions")]
    [TestCase(1, true, InclusionListEntry.Small, true, TestName = "NewPayloadV6_reports_censorship_for_the_head_without_gas_dimensions")]
    [TestCase(1, true, InclusionListEntry.WrongNonce, false, TestName = "NewPayloadV6_checks_an_unappendable_list_without_gas_dimensions")]
    [TestCase(1, true, InclusionListEntry.WrongNonce, true, TestName = "NewPayloadV6_checks_an_unappendable_list_for_the_head_without_gas_dimensions")]
    public async Task NewPayloadV6_checks_a_resent_block_on_the_recorded_gas_dimensions(
        int newPayloadCacheSize, bool dimensionsLost, InclusionListEntry entry, bool resendHead)
    {
        // Genesis is raised to the production target so the block's remaining gas exceeds the execution cap;
        // the 4M default leaves no room for a transaction big enough to tell the two rules apart.
        const ulong gasLimit = 30_000_000UL;
        AccountReadCountingStateReader accountReader = null!;
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance,
            new MergeConfig { TerminalTotalDifficulty = "0", NewPayloadCacheSize = newPayloadCacheSize },
            configurer: builder => builder
                .WithGenesisPostProcessor((genesis, _) => genesis.Header.GasLimit = gasLimit)
                // Every handler resolves its own decorated reader, so all share the first one's count.
                .AddDecorator<IStateReader>((_, reader) =>
                    accountReader ??= new AccountReadCountingStateReader(reader, TestItem.AddressC)));
        IEngineRpcModule rpc = chain.EngineRpcModule;

        // A transaction of its own, so the block's execution dimension outgrows its state dimension.
        Transaction included = Build.A.Transaction
            .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;
        byte[][] firstList = [Rlp.Encode(included).Bytes];

        ResultWrapper<GetPayloadV7Result?> payloadResult = await BuildBogotaPayload(rpc, chain.BlockTree.HeadHash,
            BuildBogotaPayloadAttributes(firstList, targetGasLimit: gasLimit, timestamp: Timestamper.UnixTime.Seconds + 2, slotNumber: 2));
        ExecutionPayloadV4 first = payloadResult.Data!.ExecutionPayload;

        await NewPayloadV6(rpc, first, payloadResult.Data!.ExecutionRequests, firstList);
        await BuildAndInsertEmptyBlock(rpc, first.BlockHash, slot: 3, finalize: !resendHead);

        Block executed = chain.BlockTree.FindBlock(first.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!;
        Assert.That(executed.Header.GasUsedPerDimension, Is.Not.Null, "the dimensions must reach the block a check is handed");
        (ulong execution, ulong state) = executed.Header.GasUsedPerDimension!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(executed.Transactions, Has.Length.EqualTo(1));
            Assert.That(Math.Max(execution, state), Is.EqualTo(executed.GasUsed), "EIP-8037 reduces the dimensions to their maximum");
            Assert.That(execution, Is.GreaterThan(state), "the max must exceed the state dimension for the two rules to differ");
        }

        // Reserves the whole remaining state dimension, and only the execution cap on the execution dimension:
        // appendable on the recorded dimensions, over budget against max(execution, state).
        Transaction censored = Build.A.Transaction
            .WithNonce(entry == InclusionListEntry.WrongNonce ? 99UL : 0UL)
            .WithGasLimit(entry is InclusionListEntry.Small or InclusionListEntry.WrongNonce ? 100_000UL : gasLimit - state).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei)
            .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyC).TestObject;
        if (entry == InclusionListEntry.Boundary)
            Assert.That(censored.GasLimit, Is.GreaterThan(gasLimit - execution), "the entry must not fit the max");

        // What a restart leaves behind: the payload cache is already past this block at size 1, and a header
        // read back from disk carries no dimensions.
        if (resendHead)
        {
            await rpc.engine_forkchoiceUpdatedV5(
                new ForkchoiceStateV1(first.BlockHash, first.BlockHash, first.BlockHash), payloadAttributes: null);
            Assert.That(chain.BlockTree.HeadHash, Is.EqualTo(first.BlockHash), "the resend must target the current head");
        }

        if (dimensionsLost)
        {
            executed.Header.GasUsedPerDimension = null;
            chain.BlockTree.FindHeader(first.BlockHash, BlockTreeLookupOptions.TotalDifficultyNotNeeded)!.GasUsedPerDimension = null;
        }

        accountReader.TrackedBlockHash = first.BlockHash;

        ResultWrapper<PayloadStatusV2> resend = await NewPayloadV6(rpc, first, [], entry == InclusionListEntry.Included ? firstList : [Rlp.Encode(censored).Bytes]);
        bool? verdict = await InclusionVerdict(rpc, resend.Data);

        bool answerUnavailable = dimensionsLost && entry == InclusionListEntry.Boundary;
        bool expectedSatisfied = entry is InclusionListEntry.Included or InclusionListEntry.WrongNonce;
        using (Assert.EnterMultipleScope())
        {
            if (dimensionsLost && entry is InclusionListEntry.WrongNonce)
                Assert.That(accountReader.AccountReads, Is.EqualTo(1), "all gas-bound passes must share the sender account read");
            Assert.That(resend.Data.Status, Is.EqualTo(PayloadStatus.Valid), "inclusion checking never holds execution validity");
            Assert.That(verdict, answerUnavailable ? Is.Null : Is.EqualTo(expectedSatisfied),
                answerUnavailable ? "without the dimensions the check must be forgotten, not guessed"
                    : expectedSatisfied ? "the entry is included or cannot be appended"
                    : "the omitted entry fits the remaining gas dimensions");
            Assert.That(executed.Header.GasUsedPerDimension, dimensionsLost ? Is.Null : Is.Not.Null, "bounds must not alter the tree's header");
        }
    }

    /// <summary>Wraps the chain's <see cref="IStateReader"/> so one block's state can be made unreadable.</summary>
    /// <remarks>
    /// <see cref="Inner"/> is assigned once the chain is built, because the reader being wrapped is itself one
    /// of the chain's components; nothing reads state during the build.
    /// </remarks>
    private sealed class HeadStateInterceptor : IStateReader
    {
        public IStateReader Inner { get; set; } = null!;

        /// <summary>Block whose state must appear pruned, as it is once the block leaves the pruning window.</summary>
        public Hash256? PrunedBlock { get; set; }

        /// <summary>Block whose account reads must throw, as they do when a subtrie node is absent under a present root.</summary>
        public Hash256? MissingNodeBlock { get; set; }

        public bool HasStateForBlock(BlockHeader? baseBlock) => !Matches(baseBlock, PrunedBlock) && Inner.HasStateForBlock(baseBlock);

        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account) =>
            Matches(baseBlock, MissingNodeBlock)
                ? throw new MissingTrieNodeException("Node missing", null, TreePath.Empty, Keccak.Zero)
                : Inner.TryGetAccount(baseBlock, address, out account);

        public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value) =>
            Inner.GetStorage(baseBlock, address, in index, out value);

        public byte[]? GetCode(Hash256 codeHash) => Inner.GetCode(codeHash);

        public byte[]? GetCode(in ValueHash256 codeHash) => Inner.GetCode(in codeHash);

        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null)
            where TCtx : struct, INodeContext<TCtx> => Inner.RunTreeVisitor(treeVisitor, baseBlock, visitingOptions, diagnostics);

        private static bool Matches(BlockHeader? header, Hash256? hash) => hash is not null && header?.Hash == hash;
    }

    /// <summary>Builds a Bogota chain whose <see cref="NewPayloadHandler"/> reads state through <paramref name="headState"/>.</summary>
    private async Task<MergeTestBlockchain> CreateBlockchainWithHeadState(HeadStateInterceptor headState,
        MergeConfig? mergeConfig = null, Action<ContainerBuilder>? configure = null)
    {
        MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, mergeConfig ?? new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder =>
            {
                builder.UpdateSingleton<NewPayloadHandler>(inner => inner.AddSingleton<IStateReader>(headState));
                configure?.Invoke(builder);
            });
        headState.Inner = chain.StateReader;
        return chain;
    }

    /// <summary>A transfer that fits an empty Bogota payload, so a list holding it is unsatisfied unless the block includes it.</summary>
    private static Transaction BuildInclusionListTransfer() => Build.A.Transaction
        .WithNonce(0).WithMaxFeePerGas(10.GWei).WithMaxPriorityFeePerGas(2.GWei).WithGasLimit(100_000)
        .WithTo(TestItem.AddressA).SignedAndResolved(TestItem.PrivateKeyB).TestObject;

    [Test]
    public async Task Account_read_count_tracks_only_the_resend_execution_context()
    {
        using MergeTestBlockchain chain = await CreateBlockchain();
        BlockHeader header = chain.BlockTree.Head!.Header;
        AccountReadCountingStateReader reader = new(chain.StateReader, TestItem.AddressC)
        {
            TrackedBlockHash = header.Hash
        };
        TaskCompletionSource<(bool Exists, AccountStruct Account)> unrelatedRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ThreadPool.UnsafeQueueUserWorkItem(_ =>
        {
            try
            {
                bool exists = reader.TryGetAccount(header, TestItem.AddressC, out AccountStruct account);
                unrelatedRead.SetResult((exists, account));
            }
            catch (Exception exception)
            {
                unrelatedRead.SetException(exception);
            }
        }, null);

        (bool exists, AccountStruct account) = await unrelatedRead.Task.WaitAsync(chain.CancellationToken);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exists, Is.True);
            Assert.That(account.Balance, Is.GreaterThan(UInt256.Zero));
            Assert.That(reader.AccountReads, Is.Zero, "background reports are outside the payload resend");
        }

        bool resendExists = reader.TryGetAccount(header, TestItem.AddressC, out AccountStruct resendAccount);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(resendExists, Is.True);
            Assert.That(resendAccount.Balance, Is.EqualTo(account.Balance));
            Assert.That(reader.AccountReads, Is.EqualTo(1), "the resend still charges its real account read");
        }
    }

    /// <summary>Entries exercising known and ambiguous compliance after gas-dimension cache loss.</summary>
    public enum InclusionListEntry { Boundary, Included, Small, WrongNonce }

    /// <summary>Counts a selected sender's account reads during one payload resend.</summary>
    private sealed class AccountReadCountingStateReader(IStateReader inner, Address sender) : IStateReader
    {
        private int _accountReads;
        private readonly AsyncLocal<Hash256?> _trackedBlockHash = new();
        public Hash256? TrackedBlockHash
        {
            get => _trackedBlockHash.Value;
            set => _trackedBlockHash.Value = value;
        }
        public int AccountReads => Volatile.Read(ref _accountReads);

        public bool TryGetAccount(BlockHeader? baseBlock, Address address, out AccountStruct account)
        {
            if (TrackedBlockHash is { } hash && baseBlock?.Hash == hash && address == sender)
                Interlocked.Increment(ref _accountReads);
            return inner.TryGetAccount(baseBlock, address, out account);
        }

        public bool HasStateForBlock(BlockHeader? baseBlock) => inner.HasStateForBlock(baseBlock);
        public void GetStorage(BlockHeader? baseBlock, Address address, in UInt256 index, out UInt256 value) =>
            inner.GetStorage(baseBlock, address, in index, out value);
        public byte[]? GetCode(Hash256 codeHash) => inner.GetCode(codeHash);
        public byte[]? GetCode(in ValueHash256 codeHash) => inner.GetCode(in codeHash);
        public void RunTreeVisitor<TCtx>(ITreeVisitor<TCtx> treeVisitor, BlockHeader? baseBlock, VisitingOptions? visitingOptions = null, VisitingStats? diagnostics = null)
            where TCtx : struct, INodeContext<TCtx> => inner.RunTreeVisitor(treeVisitor, baseBlock, visitingOptions, diagnostics);
    }
    /// <summary>
    /// A block that runs EIP-8360 TCREATE twice at one address and then pays the wiped account is accepted by a
    /// second node in the fixture's execution mode, and its witness re-executes statelessly to the same state root.
    /// </summary>
    [Test]
    public virtual async Task Eip8360_tcreate_block_validates_on_another_node_and_from_its_witness()
    {
        IReleaseSpec spec = new OverridableReleaseSpec(Bogota.Instance) { IsEip8360Enabled = true };
        using MergeTestBlockchain producer = await CreateBlockchain(spec, new MergeConfig { TerminalTotalDifficulty = "0" });
        using MergeTestBlockchain validator = await CreateBlockchain(spec, new MergeConfig { TerminalTotalDifficulty = "0" });

        byte[] salt = new UInt256(8360).ToBigEndian();
        byte[] runtime = Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;
        // Sends 2 of its endowment of 5 out and writes transient storage before deploying.
        byte[] init = Prepare.EvmCode.CallWithValue(TestItem.AddressB, 100_000, 2).Op(Instruction.POP).SSTORE(0, [7]).ForInitOf(runtime).Done;
        ulong nonce = producer.ReadOnlyState.GetNonce(TestItem.AddressA);
        Address factory = ContractAddress.From(TestItem.AddressA, nonce);
        Address tcreated = ContractAddress.FromTransientCreate(factory, salt, init);
        byte[] factoryCode = Prepare.EvmCode
            .TCreate(init, salt, 5).PushData(0).Op(Instruction.SSTORE)
            .PushData(32).PushData(0).PushData(0).PushData(0).PushData(0).PushData(tcreated).PushData(200_000)
            .Op(Instruction.CALL).Op(Instruction.POP)
            .PushData(0).Op(Instruction.MLOAD).PushData(1).Op(Instruction.SSTORE)
            .STOP().Done;
        Transaction[] txs =
        [
            Build.A.Transaction.WithNonce(nonce).WithCode(Prepare.EvmCode.ForInitOf(factoryCode).Done).WithGasLimit(800_000)
                .SignedAndResolved(producer.EthereumEcdsa, TestItem.PrivateKeyA).TestObject,
            Build.A.Transaction.WithNonce(nonce + 1).WithTo(factory).WithValue(5).WithGasLimit(900_000)
                .SignedAndResolved(producer.EthereumEcdsa, TestItem.PrivateKeyA).TestObject,
            Build.A.Transaction.WithNonce(nonce + 2).WithTo(factory).WithValue(5).WithGasLimit(900_000)
                .SignedAndResolved(producer.EthereumEcdsa, TestItem.PrivateKeyA).TestObject,
            Build.A.Transaction.WithNonce(nonce + 3).WithTo(tcreated).WithValue(1).WithGasLimit(900_000)
                .SignedAndResolved(producer.EthereumEcdsa, TestItem.PrivateKeyA).TestObject,
        ];
        foreach (Transaction tx in txs)
        {
            Assert.That(producer.TxPool.SubmitTx(tx, TxHandlingOptions.None), Is.EqualTo(AcceptTxResult.Accepted));
        }

        Hash256 head = producer.BlockTree.HeadHash;
        Task improved = producer.WaitForImprovedBlock(head, minTransactions: txs.Length);
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await producer.EngineRpcModule.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(head, Keccak.Zero, head), BuildBogotaPayloadAttributes(inclusionList: []));
        Assert.That(fcu.Result.ResultType, Is.EqualTo(ResultType.Success), fcu.Result.Error);
        await improved;
        ResultWrapper<GetPayloadV7Result?> payload = await producer.EngineRpcModule.engine_getPayloadV7(Bytes.FromHexString(fcu.Data.PayloadId!));
        ExecutionPayloadV4 executionPayload = payload.Data!.ExecutionPayload;
        Assert.That(executionPayload.Transactions, Has.Length.EqualTo(txs.Length));

        BlockHeader parent = validator.BlockTree.Head!.Header;
        ResultWrapper<NewPayloadWithWitnessV1Result> result = await validator.EngineRpcModule.engine_newPayloadWithWitnessV6(
            executionPayload, [], Keccak.Zero, payload.Data.ExecutionRequests, AtPositions(), []);
        Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid), result.Data.ValidationError);

        Block suggested = executionPayload.TryGetBlock().Data!;
        StatelessBlockProcessingEnv stateless = new(result.Data.ExecutionWitness!, validator.SpecProvider, Always.Valid, LimboLogs.Instance);
        Hash256? statelessRoot;
        using (stateless.WorldState.BeginScope(parent))
        {
            (Block processed, _) = stateless.BlockProcessor.ProcessOne(suggested, ProcessingOptions.ProducingBlock, NullBlockTracer.Instance, validator.SpecProvider.GetSpec(suggested.Header));
            statelessRoot = processed.Header.StateRoot;
        }

        await validator.EngineRpcModule.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(executionPayload.BlockHash, executionPayload.BlockHash, executionPayload.BlockHash), null);
        BlockHeader block = validator.BlockTree.Head!.Header;
        validator.StateReader.GetStorage(block, factory, 0, out UInt256 lastTCreate);
        validator.StateReader.GetStorage(block, factory, 1, out UInt256 calledCode);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(statelessRoot, Is.EqualTo(executionPayload.StateRoot), "the witness covers the end-of-transaction wipe");
            Assert.That(block.Hash, Is.EqualTo(executionPayload.BlockHash));
            Assert.That(lastTCreate, Is.EqualTo(new UInt256(tcreated.Bytes, isBigEndian: true)), "the second TCREATE in the block succeeds");
            Assert.That(calledCode, Is.EqualTo((UInt256)42));
            Assert.That(validator.StateReader.GetBalance(block, tcreated), Is.EqualTo((UInt256)7));
            Assert.That(validator.StateReader.GetNonce(block, tcreated), Is.Zero);
            Assert.That(validator.StateReader.GetCodeHash(block, tcreated), Is.EqualTo(Keccak.OfAnEmptyString.ValueHash256));
        }
    }

    private async Task<ExecutionPayloadV4> BuildAndInsertEmptyBlock(IEngineRpcModule rpc, Hash256 parent, ulong slot,
        bool finalize = true, Hash256? finalizedHash = null)
    {
        ResultWrapper<GetPayloadV7Result?> payloadResult = await BuildBogotaPayload(rpc, parent,
            BuildBogotaPayloadAttributes(inclusionList: [], timestamp: Timestamper.UnixTime.Seconds + slot, slotNumber: slot), finalizedHash ?? parent);
        ExecutionPayloadV4 payload = payloadResult.Data!.ExecutionPayload;

        await NewPayloadV6(rpc, payload, payloadResult.Data!.ExecutionRequests, []);
        Hash256 checkpoint = finalize ? payload.BlockHash : finalizedHash ?? parent;
        await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(payload.BlockHash, checkpoint, checkpoint), payloadAttributes: null);
        return payload;
    }

    /// <summary>Builds an empty Bogota payload on <paramref name="parent"/> without sending it back.</summary>
    private async Task<ExecutionPayloadV4> BuildEmptyPayload(IEngineRpcModule rpc, Hash256 parent) =>
        (await BuildBogotaPayload(rpc, parent, BuildBogotaPayloadAttributes(inclusionList: []))).Data!.ExecutionPayload;

    private static async Task<ResultWrapper<GetPayloadV7Result?>> BuildBogotaPayload(IEngineRpcModule rpc, Hash256 parent, PayloadAttributes attributes,
        Hash256? finalizedHash = null)
    {
        ResultWrapper<ForkchoiceUpdatedV1Result> fcu = await rpc.engine_forkchoiceUpdatedV5(
            new ForkchoiceStateV1(parent, Keccak.Zero, finalizedHash ?? parent), attributes);
        Assert.That(fcu.Data?.PayloadId, Is.Not.Null, fcu.Result.Error);
        return await rpc.engine_getPayloadV7(Bytes.FromHexString(fcu.Data!.PayloadId!));
    }

    /// <summary>Sends <paramref name="payload"/> with <paramref name="inclusionList"/> packed into committee positions.</summary>
    private static Task<ResultWrapper<PayloadStatusV2>> NewPayloadV6(IEngineRpcModule rpc, ExecutionPayloadV4 payload, byte[][]? executionRequests,
        byte[][] inclusionList, OmissionClaim[]? claims = null) =>
        rpc.engine_newPayloadV6(payload, [], Keccak.Zero, executionRequests, Pack(inclusionList), claims ?? []);

    /// <summary>Runs the deferred inclusion check on <paramref name="payload"/> directly, its access list read from the
    /// store unless <paramref name="carried"/> is given.</summary>
    private static Task<bool?> Recheck(MergeTestBlockchain chain, ExecutionPayloadV4 payload, byte[][] inclusionList, ReadOnlyBlockAccessList? carried = null)
    {
        Block block = payload.TryGetBlock().Data!;
        block.BlockAccessList = carried;
        return chain.Container.Resolve<IInclusionListComplianceEvaluator>()
            .EvaluateAsync(block, InclusionListDecoder.DecodeLists(Pack(inclusionList)), [], CancellationToken.None);
    }

    /// <summary>The completed verdict of the inclusion check <paramref name="status"/> names, polled while it is pending.</summary>
    /// <returns><c>null</c> once the check is unknown, as an undecidable one becomes.</returns>
    private static async Task<bool?> InclusionVerdict(IEngineRpcModule rpc, PayloadStatusV2 status)
    {
        Assert.That(status.InclusionCheckId, Is.Not.Null, "a VALID payload names its inclusion check");
        if (status.InclusionListSatisfied is { } completed) return completed;

        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        while (true)
        {
            ResultWrapper<bool?> verdict = await rpc.engine_getInclusionListVerdictV1(status.InclusionCheckId!);
            if (verdict.ErrorCode == MergeErrorCodes.UnknownInclusionCheck) return null;
            Assert.That(verdict.Result.ResultType, Is.EqualTo(ResultType.Success), verdict.Result.Error);
            if (verdict.Data is { } done) return done;
            await Task.Delay(10, timeout.Token);
        }
    }

    private PayloadAttributes BuildBogotaPayloadAttributes(byte[][] inclusionList, ulong targetGasLimit = 30_000_000UL, ulong? timestamp = null, ulong slotNumber = 1) =>
        BuildBogotaPayloadAttributes(Pack(inclusionList), targetGasLimit, timestamp, slotNumber);

    private PayloadAttributes BuildBogotaPayloadAttributes(byte[][][]? inclusionLists, ulong targetGasLimit = 30_000_000UL, ulong? timestamp = null, ulong slotNumber = 1) => new()
    {
        Timestamp = timestamp ?? Timestamper.UnixTime.Seconds,
        PrevRandao = Keccak.Zero,
        SuggestedFeeRecipient = TestItem.AddressC,
        Withdrawals = [],
        ParentBeaconBlockRoot = Keccak.Zero,
        SlotNumber = slotNumber,
        // V4 attributes require TargetGasLimit.
        TargetGasLimit = targetGasLimit,
        InclusionLists = inclusionLists,
    };

    /// <summary>One list per committee position: <paramref name="inclusionList"/> packed in order, each position filled
    /// up to <c>MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST</c> before the next.</summary>
    [return: NotNullIfNotNull(nameof(inclusionList))]
    private static byte[][][]? Pack(byte[][]? inclusionList)
    {
        if (inclusionList is null) return null;

        List<byte[]>[] positions = [.. Enumerable.Range(0, Eip7805Constants.InclusionListCommitteeSize).Select(static _ => new List<byte[]>())];
        int position = 0;
        long bytes = 0;
        foreach (byte[] transaction in inclusionList)
        {
            if (bytes + transaction.Length > Eip7805Constants.MaxBytesPerInclusionList && position < positions.Length - 1)
            {
                position++;
                bytes = 0;
            }
            bytes += transaction.Length;
            positions[position].Add(transaction);
        }
        return [.. positions.Select(static list => list.ToArray())];
    }

    /// <summary>One list per committee position: <paramref name="inclusionLists"/> from position 0, the rest empty.</summary>
    private static byte[][][] AtPositions(params byte[][][] inclusionLists) =>
        [.. inclusionLists, .. Enumerable.Repeat<byte[][]>([], Eip7805Constants.InclusionListCommitteeSize - inclusionLists.Length)];
}
