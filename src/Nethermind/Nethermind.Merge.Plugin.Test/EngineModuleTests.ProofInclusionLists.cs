// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Test;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;
using NSubstitute;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [TestCase("missing-proof")]
    [TestCase("bad-proof")]
    [TestCase("wrong-commitment")]
    public async Task NewPayload_bad_inclusion_proof_does_not_invalidate_the_block(string scenario)
    {
        OverridableReleaseSpec spec = InclusionProofEngineSpec();
        using MergeTestBlockchain chain = await CreateBlockchain(spec, new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        Transaction transaction = InclusionDependencyTransaction(chain.SpecProvider.ChainId);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.ForTransaction(transaction));
        InclusionListV1[] lists = [InclusionList(3, transaction, scenario == "missing-proof" ? null
            : new RecursiveStarkV1 { StarkProof = [2], DepsHash = scenario == "wrong-commitment" ? Keccak.Zero : new Hash256(depsHash) })];
        PayloadAttributes attributes = BuildPerListPayloadAttributes(lists);
        ResultWrapper<ForkchoiceUpdatedV2Result> build = await rpc.engine_forkchoiceUpdatedV6(new(parent, Keccak.Zero, parent), attributes);
        Assert.That(build.Result.ResultType, Is.EqualTo(ResultType.Success), build.Result.Error);
        GetPayloadV6Result produced = (await rpc.engine_getPayloadV6(Bytes.FromHexString(build.Data.PayloadId!))).Data!;
        Assert.That(produced.ExecutionPayload.InclusionLists, Is.Null, "per-list proofs are inputs, not echoed by getPayload");
        Assert.That(produced.ExecutionPayload.Transactions, Is.Empty);

        ResultWrapper<PayloadStatusV2> result = await rpc.engine_newPayloadV7(produced.ExecutionPayload,
            [], Keccak.Zero, produced.ExecutionRequests, lists);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(result.Data.InclusionListSatisfied, Is.True);
            Assert.That(result.Data.LatestValidHash, Is.EqualTo(produced.ExecutionPayload.BlockHash));
        }
    }

    // EIPs#12475: the activating fork defines new Engine API versions for per-list inclusion lists, and existing versions
    // keep their encodings, so V6 and FCUv5 attributes are refused once EIP-8288 and EIP-7805 are both active.
    [Test]
    public async Task Flattened_inclusion_list_versions_are_refused_once_lists_carry_proofs()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        ResultWrapper<ForkchoiceUpdatedV2Result> flattened = await rpc.engine_forkchoiceUpdatedV5(new(parent, Keccak.Zero, parent),
            BuildBogotaPayloadAttributes([]));
        ResultWrapper<ForkchoiceUpdatedV2Result> perList = await rpc.engine_forkchoiceUpdatedV6(new(parent, Keccak.Zero, parent),
            BuildPerListPayloadAttributes([]));
        Assert.That(flattened.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
        Assert.That(perList.Result.ResultType, Is.EqualTo(ResultType.Success), perList.Result.Error);
        GetPayloadV6Result produced = (await rpc.engine_getPayloadV6(Bytes.FromHexString(perList.Data.PayloadId!))).Data!;

        ResultWrapper<PayloadStatusV2> v6 = await rpc.engine_newPayloadV6(produced.ExecutionPayload, [], Keccak.Zero, produced.ExecutionRequests, []);
        ResultWrapper<PayloadStatusV2> missingLists = await rpc.engine_newPayloadV7(produced.ExecutionPayload, [], Keccak.Zero, produced.ExecutionRequests, null);
        // Answered as V7, not routed to V6 (which would report -38005) by the missing lists.
        using ResultWrapper<NewPayloadWithWitnessV1Result> witnessMissingLists = await rpc.engine_newPayloadWithWitnessV7(
            produced.ExecutionPayload, [], Keccak.Zero, produced.ExecutionRequests, null);
        ResultWrapper<PayloadStatusV2> v7 = await rpc.engine_newPayloadV7(produced.ExecutionPayload, [], Keccak.Zero, produced.ExecutionRequests, []);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(v6.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
            Assert.That(missingLists.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(witnessMissingLists.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
            Assert.That(witnessMissingLists.Result.Error, Is.EqualTo(missingLists.Result.Error));
            Assert.That(v7.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(v7.Data.InclusionListSatisfied, Is.True);
        }
    }

    [Test]
    public async Task Per_list_versions_are_refused_without_eip8288()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        ResultWrapper<ForkchoiceUpdatedV2Result> perList = await rpc.engine_forkchoiceUpdatedV6(new(parent, Keccak.Zero, parent),
            BuildPerListPayloadAttributes([]));
        ResultWrapper<ForkchoiceUpdatedV2Result> build = await rpc.engine_forkchoiceUpdatedV5(new(parent, Keccak.Zero, parent),
            BuildBogotaPayloadAttributes([]));
        GetPayloadV6Result produced = (await rpc.engine_getPayloadV6(Bytes.FromHexString(build.Data.PayloadId!))).Data!;
        ResultWrapper<PayloadStatusV2> v7 = await rpc.engine_newPayloadV7(produced.ExecutionPayload, [], Keccak.Zero, produced.ExecutionRequests, []);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(perList.Result.ResultType, Is.EqualTo(ResultType.Failure));
            Assert.That(v7.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
        }
    }

    // The execution-apis style InclusionListV1 wire form: committeeIndex, transactions and recursiveStark {starkProof, depsHash}.
    [TestCase("0xf", "VALID")]
    [TestCase("0x10", "-32602")]
    public async Task NewPayloadV7_reads_inclusion_lists_from_json(string committeeIndex, string expected)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        ResultWrapper<ForkchoiceUpdatedV2Result> build = await rpc.engine_forkchoiceUpdatedV6(new(parent, Keccak.Zero, parent),
            BuildPerListPayloadAttributes([]));
        GetPayloadV6Result produced = (await rpc.engine_getPayloadV6(Bytes.FromHexString(build.Data.PayloadId!))).Data!;
        Transaction transaction = InclusionDependencyTransaction(chain.SpecProvider.ChainId);
        string encoded = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true);
        string depsHash = new Hash256(Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.ForTransaction(transaction))).ToString();
        string lists = $$$"""[{"committeeIndex":"{{{committeeIndex}}}","transactions":["{{{encoded}}}"],"recursiveStark":{"starkProof":"0x02","depsHash":"{{{depsHash}}}"}}]""";

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV7",
            JsonDocument.Parse(chain.JsonSerializer.Serialize(produced.ExecutionPayload)).RootElement,
            JsonDocument.Parse("[]").RootElement, Keccak.Zero.ToString(),
            JsonDocument.Parse(chain.JsonSerializer.Serialize(produced.ExecutionRequests)).RootElement,
            JsonDocument.Parse(lists).RootElement);

        Assert.That(response, Does.Contain(expected));
        if (expected == "VALID") Assert.That(response, Does.Contain("\"inclusionListSatisfied\":true"));
    }

    [TestCase("duplicate-position")]
    [TestCase("descending-position")]
    [TestCase("position-out-of-range")]
    [TestCase("empty-transaction")]
    [TestCase("oversized-list")]
    [TestCase("too-many-lists")]
    public async Task NewPayloadV7_rejects_malformed_lists_as_invalid_params(string scenario)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        ResultWrapper<ForkchoiceUpdatedV2Result> build = await rpc.engine_forkchoiceUpdatedV6(new(parent, Keccak.Zero, parent),
            BuildPerListPayloadAttributes([]));
        GetPayloadV6Result produced = (await rpc.engine_getPayloadV6(Bytes.FromHexString(build.Data.PayloadId!))).Data!;
        InclusionListV1[] lists = scenario switch
        {
            "duplicate-position" => [new() { CommitteeIndex = 1, Transactions = [] }, new() { CommitteeIndex = 1, Transactions = [] }],
            "descending-position" => [new() { CommitteeIndex = 2, Transactions = [] }, new() { CommitteeIndex = 1, Transactions = [] }],
            "position-out-of-range" => [new() { CommitteeIndex = Eip7805Constants.InclusionListCommitteeSize, Transactions = [] }],
            "empty-transaction" => [new() { Transactions = [[]] }],
            "oversized-list" => [new() { Transactions = [new byte[Eip7805Constants.MaxBytesPerInclusionList + 1]] }],
            _ => [.. Enumerable.Range(0, Eip7805Constants.InclusionListCommitteeSize + 1).Select(i => new InclusionListV1 { CommitteeIndex = (ulong)i, Transactions = [] })]
        };

        ResultWrapper<PayloadStatusV2> result = await rpc.engine_newPayloadV7(produced.ExecutionPayload, [], Keccak.Zero, produced.ExecutionRequests, lists);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [TestCase("missing-proof")]
    [TestCase("bad-proof")]
    [TestCase("empty-proof")]
    public async Task Forkchoice_applies_checkpoints_before_inclusion_proof_attributes(string scenario)
    {
        OverridableReleaseSpec spec = InclusionProofEngineSpec();
        using MergeTestBlockchain chain = await CreateBlockchain(spec, new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        ResultWrapper<ForkchoiceUpdatedV2Result> build = await rpc.engine_forkchoiceUpdatedV6(
            new(parent, Keccak.Zero, parent), BuildPerListPayloadAttributes([]));
        GetPayloadV6Result produced = (await rpc.engine_getPayloadV6(Bytes.FromHexString(build.Data.PayloadId!))).Data!;
        ResultWrapper<PayloadStatusV2> imported = await rpc.engine_newPayloadV7(produced.ExecutionPayload, [], Keccak.Zero, produced.ExecutionRequests, []);
        Assert.That(imported.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Transaction transaction = InclusionDependencyTransaction(chain.SpecProvider.ChainId);
        RecursiveStarkV1? proof = scenario == "missing-proof" ? null : new()
        {
            StarkProof = scenario == "empty-proof" ? [] : [2],
            DepsHash = new Hash256(Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.ForTransaction(transaction)))
        };
        PayloadAttributes attributes = BuildPerListPayloadAttributes([InclusionList(0, transaction, proof)],
            timestamp: produced.ExecutionPayload.Timestamp + 12, slotNumber: 2);
        Hash256 head = produced.ExecutionPayload.BlockHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> result = await rpc.engine_forkchoiceUpdatedV6(new(head, head, head), attributes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.HeadHash, Is.EqualTo(head));
            Assert.That(chain.BlockTree.SafeHash, Is.EqualTo(head));
            Assert.That(chain.BlockTree.FinalizedHash, Is.EqualTo(head));
            Assert.That(result.Result.ResultType, Is.EqualTo(scenario == "empty-proof" ? ResultType.Failure : ResultType.Success));
            if (scenario == "empty-proof") Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidPayloadAttributes));
        }
    }

    [TestCase("wrong-version")]
    [TestCase("invalid-attributes")]
    [TestCase("valid")]
    public async Task Forkchoice_retains_inclusion_lists_only_for_valid_attributes(string scenario)
    {
        IInclusionListTxSource source = Substitute.For<IInclusionListTxSource>();
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier())
                .AddSingleton<IInclusionListTxSource>(source));
        Hash256 head = chain.BlockTree.HeadHash!;
        PayloadAttributes attributes = BuildPerListPayloadAttributes([new() { CommitteeIndex = 0, Transactions = [] }]);
        if (scenario == "invalid-attributes") attributes.ParentBeaconBlockRoot = null;

        ResultWrapper<ForkchoiceUpdatedV2Result> result = scenario == "wrong-version"
            ? await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(new(head, Keccak.Zero, head), attributes)
            : await chain.EngineRpcModule.engine_forkchoiceUpdatedV6(new(head, Keccak.Zero, head), attributes);

        Assert.That(result.Result.ResultType, Is.EqualTo(scenario == "valid" ? ResultType.Success : ResultType.Failure));
        source.ReceivedWithAnyArgs(scenario == "valid" ? 1 : 0).Set(default!, default!, default);
    }

    private static InclusionListV1 InclusionList(ulong committeeIndex, Transaction transaction, RecursiveStarkV1? proof) => new()
    {
        CommitteeIndex = committeeIndex,
        Transactions = [TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes],
        RecursiveStark = proof
    };

    private PayloadAttributes BuildPerListPayloadAttributes(InclusionListV1[] lists, ulong? timestamp = null, ulong slotNumber = 1)
    {
        PayloadAttributes attributes = BuildBogotaPayloadAttributes(null!, timestamp: timestamp, slotNumber: slotNumber);
        attributes.InclusionListTransactions = null;
        attributes.InclusionLists = lists;
        return attributes;
    }

    private static OverridableReleaseSpec InclusionProofEngineSpec() => new(Bogota.Instance)
    {
        IsEip8141Enabled = true,
        IsEip8288Enabled = true,
        IsEip8250Enabled = true,
        IsEip8272Enabled = true,
        IsEip7906Enabled = true
    };

    private static Transaction InclusionDependencyTransaction(ulong chainId)
    {
        byte[] data = new byte[Eip8288Constants.DependencyTripleLength];
        data[31] = Eip8288Constants.LeanSphincsScheme;
        TxFrame[] frames = [new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero, data)];
        return new Transaction
        {
            Type = TxType.FrameTx,
            ChainId = chainId,
            SenderAddress = TestItem.AddressA,
            NonceKeys = [UInt256.Zero],
            Frames = frames,
            FrameSignatures = [],
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
    }

    private sealed class EngineListProofVerifier : ILeanProofVerifier
    {
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => proof is [1];
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) => [1];
    }
}
