// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Consensus.ProofAggregation;
using Nethermind.Consensus.Producers;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
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
    // EIPs#12475: a list's proof never affects block validity. An empty or invalid proof obligates no dependency-bearing entry; a
    // verified covering proof obligates it, and this entry, lacking a payment frame, is never appendable, so every scenario is
    // satisfied. A malformed entry or invalid proof is scoped to its list, not the request.
    [TestCase("empty-proof")]
    [TestCase("invalid-proof")]
    [TestCase("covering-proof")]
    [TestCase("empty-transaction")]
    public async Task NewPayload_bad_inclusion_proof_does_not_invalidate_the_block(string scenario)
    {
        OverridableReleaseSpec spec = InclusionProofEngineSpec();
        using MergeTestBlockchain chain = await CreateBlockchain(spec, new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        Transaction transaction = InclusionDependencyTransaction(chain.SpecProvider.ChainId);
        RecursiveStarkV1 proof = scenario switch
        {
            "invalid-proof" => Proof([2], transaction),
            "covering-proof" => Proof([1], transaction),
            _ => RecursiveStarkV1.Empty
        };
        InclusionListV1[] lists = [InclusionList(transaction, proof)];
        if (scenario == "empty-transaction") lists[0].Transactions = [.. lists[0].Transactions!, []];
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

    // The execution-apis InclusionListV1 wire form: transactions and recursiveStark {starkProof, proofDeps}, each proofDeps entry
    // 96 bytes.
    [TestCase("covering", "VALID")]
    [TestCase("empty", "VALID")]
    [TestCase("short-dependency", "-32602")]
    [TestCase("missing-proof", "-32602")]
    public async Task NewPayloadV7_reads_inclusion_lists_from_json(string scenario, string expected)
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
        byte[][] proofDeps = Proof([1], transaction).ProofDeps!;
        if (scenario == "short-dependency") proofDeps[0] = proofDeps[0][..^1];
        string deps = string.Join(",", proofDeps.Select(static dependency => $"\"{dependency.ToHexString(true)}\""));
        string recursiveStark = scenario switch
        {
            "covering" or "short-dependency" => $$""","recursiveStark":{"starkProof":"0x01","proofDeps":[{{deps}}]}""",
            "empty" => ""","recursiveStark":{"starkProof":"0x","proofDeps":[]}""",
            _ => ""
        };
        string lists = $$$"""[{"transactions":["{{{encoded}}}"]{{{recursiveStark}}}}]""";

        string response = await RpcTest.TestSerializedRequest(rpc, "engine_newPayloadV7",
            JsonDocument.Parse(chain.JsonSerializer.Serialize(produced.ExecutionPayload)).RootElement,
            JsonDocument.Parse("[]").RootElement, Keccak.Zero.ToString(),
            JsonDocument.Parse(chain.JsonSerializer.Serialize(produced.ExecutionRequests)).RootElement,
            JsonDocument.Parse(lists).RootElement);

        Assert.That(response, Does.Contain(expected));
        if (expected == "VALID") Assert.That(response, Does.Contain("\"inclusionListSatisfied\":true"));
    }

    // EIPs#12475: the serialized InclusionListProof container, two 4-byte offsets and both fields, fits MAX_IL_PROOF_BYTES;
    // one byte more is refused, independently of MAX_TRANSACTIONS_BYTES_PER_INCLUSION_LIST.
    [TestCase("proof-at-limit", true)]
    [TestCase("proof-with-dependency-at-limit", true)]
    [TestCase("proof-over-limit", false)]
    [TestCase("proof-with-dependency-over-limit", false)]
    [TestCase("oversized-list", false)]
    [TestCase("too-many-lists", false)]
    [TestCase("missing-proof", false)]
    public async Task NewPayloadV7_bounds_each_list(string scenario, bool accepted)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        ResultWrapper<ForkchoiceUpdatedV2Result> build = await rpc.engine_forkchoiceUpdatedV6(new(parent, Keccak.Zero, parent),
            BuildPerListPayloadAttributes([]));
        GetPayloadV6Result produced = (await rpc.engine_getPayloadV6(Bytes.FromHexString(build.Data.PayloadId!))).Data!;
        const int Offsets = 2 * sizeof(uint);
        byte[][] dependency = [new byte[Eip8288Constants.DependencyTripleLength]];
        InclusionListV1[] lists = scenario switch
        {
            "proof-at-limit" => [ProofOfSize(Eip8288Constants.MaxIlProofBytes - Offsets, [])],
            "proof-with-dependency-at-limit" => [ProofOfSize(Eip8288Constants.MaxIlProofBytes - Offsets - Eip8288Constants.DependencyTripleLength, dependency)],
            "proof-over-limit" => [ProofOfSize(Eip8288Constants.MaxIlProofBytes - Offsets + 1, [])],
            "proof-with-dependency-over-limit" => [ProofOfSize(Eip8288Constants.MaxIlProofBytes - Offsets - Eip8288Constants.DependencyTripleLength + 1, dependency)],
            "oversized-list" => [new() { Transactions = [new byte[Eip7805Constants.MaxBytesPerInclusionList + 1]], RecursiveStark = RecursiveStarkV1.Empty }],
            "missing-proof" => [new() { Transactions = [] }],
            _ => [.. Enumerable.Range(0, Eip7805Constants.InclusionListCommitteeSize + 1).Select(static _ => new InclusionListV1 { Transactions = [], RecursiveStark = RecursiveStarkV1.Empty })]
        };

        ResultWrapper<PayloadStatusV2> result = await rpc.engine_newPayloadV7(produced.ExecutionPayload, [], Keccak.Zero, produced.ExecutionRequests, lists);

        if (accepted) Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid), result.Result.Error);
        else Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));

        static InclusionListV1 ProofOfSize(int proofBytes, byte[][] proofDeps) =>
            new() { Transactions = [], RecursiveStark = new RecursiveStarkV1 { StarkProof = new byte[proofBytes], ProofDeps = proofDeps } };
    }

    [TestCase("empty-proof")]
    [TestCase("bad-proof")]
    [TestCase("missing-proof")]
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
        RecursiveStarkV1? proof = scenario switch
        {
            "missing-proof" => null,
            "bad-proof" => Proof([2], transaction),
            _ => RecursiveStarkV1.Empty
        };
        PayloadAttributes attributes = BuildPerListPayloadAttributes([InclusionList(transaction, proof)],
            timestamp: produced.ExecutionPayload.Timestamp + 12, slotNumber: 2);
        Hash256 head = produced.ExecutionPayload.BlockHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> result = await rpc.engine_forkchoiceUpdatedV6(new(head, head, head), attributes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.HeadHash, Is.EqualTo(head));
            Assert.That(chain.BlockTree.SafeHash, Is.EqualTo(head));
            Assert.That(chain.BlockTree.FinalizedHash, Is.EqualTo(head));
            Assert.That(result.Result.ResultType, Is.EqualTo(scenario == "missing-proof" ? ResultType.Failure : ResultType.Success));
            if (scenario == "missing-proof") Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidPayloadAttributes));
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
        PayloadAttributes attributes = BuildPerListPayloadAttributes([new() { Transactions = [], RecursiveStark = RecursiveStarkV1.Empty }]);
        if (scenario == "invalid-attributes") attributes.ParentBeaconBlockRoot = null;

        ResultWrapper<ForkchoiceUpdatedV2Result> result = scenario == "wrong-version"
            ? await chain.EngineRpcModule.engine_forkchoiceUpdatedV5(new(head, Keccak.Zero, head), attributes)
            : await chain.EngineRpcModule.engine_forkchoiceUpdatedV6(new(head, Keccak.Zero, head), attributes);

        Assert.That(result.Result.ResultType, Is.EqualTo(scenario == "valid" ? ResultType.Success : ResultType.Failure));
        source.ReceivedWithAnyArgs(scenario == "valid" ? 1 : 0).Set(default!, default!, default);
    }

    // EIPs#12475: engine_getInclusionListV2 returns the list's transactions with its recursiveStark. Without a dependency-bearing
    // pool transaction the proof is empty: "0x" with no proofDeps.
    [Test]
    public async Task GetInclusionListV2_returns_transactions_with_an_empty_proof()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        Transaction transaction = Build.A.Transaction.WithNonce(0).WithMaxFeePerGas(100.GWei).WithMaxPriorityFeePerGas(1.GWei)
            .WithType(TxType.EIP1559).WithChainId(chain.SpecProvider.ChainId).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        chain.TxPool.SubmitTx(transaction, TxPool.TxHandlingOptions.None);

        string response = await RpcTest.TestSerializedRequest(chain.EngineRpcModule, "engine_getInclusionListV2");

        Assert.That(response, Does.Contain($$$"""{"transactions":["{{{TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true)}}}"],"recursiveStark":{"starkProof":"0x","proofDeps":[]}}"""));
    }

    // EIPs#12475 end to end: a dependency-bearing pool transaction admitted with a verified mode-1 wrapper is listed under that
    // wrapper's proof, which then obligates it. The block built with the list includes it under a proof covering its dependency,
    // and a sibling block omitting it fails the list. Tampering with the signed proof leaves the list no dependency-bearing
    // obligation, so the same omission satisfies it.
    [TestCase(true)]
    [TestCase(false)]
    public async Task GetInclusionListV2_lists_dependency_bearing_transactions_under_a_held_wrapper_proof(bool validListProof)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new DepsHashProofVerifier()));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        ExecutionPayloadV3 omitting = await BuildPayload(chain, parent, []);

        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("il-dependency"), default);
        Transaction transaction = ExecutableDependencyTransaction(chain.SpecProvider.ChainId, dependency);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash([dependency]);
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [transaction],
            Deps = [dependency],
            Mode = MempoolWrapper.ModeRecursive,
            RecursiveStark = new RecursiveStark(depsHash.ToByteArray(), new Hash256(depsHash))
        }).Bytes;
        ProofWrapperAcceptance accepted = await chain.Container.Resolve<ProofWrapperService>().AcceptDetailedAsync(wrapper);
        Assert.That(accepted.Result.IsSuccess, Is.True, accepted.Result.Error);

        InclusionListV1 list;
        using (ResultWrapper<GetInclusionListV2Result> listed = await rpc.engine_getInclusionListV2(parent))
        {
            Assert.That(listed.Result.ResultType, Is.EqualTo(ResultType.Success), listed.Result.Error);
            list = new InclusionListV1
            {
                Transactions = [.. listed.Data.Transactions.Select(static bytes => bytes.AsSpan().ToArray())],
                RecursiveStark = listed.Data.RecursiveStark
            };
        }
        using (Assert.EnterMultipleScope())
        {
            Assert.That(list.Transactions, Is.EqualTo(new[] { TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes }));
            Assert.That(list.RecursiveStark!.StarkProof, Is.EqualTo(depsHash.ToByteArray()), "the wrapper's verified proof is reused");
            Assert.That(list.RecursiveStark.ProofDeps, Is.EqualTo(new[] { Eip8288Dependencies.Serialize([dependency]) }));
        }
        if (!validListProof) list.RecursiveStark = new RecursiveStarkV1 { StarkProof = [0xee], ProofDeps = list.RecursiveStark.ProofDeps };

        ExecutionPayloadV3 including = await BuildPayload(chain, parent, [list], minTransactions: 1);
        ResultWrapper<PayloadStatusV2> includingStatus = await rpc.engine_newPayloadV7((ExecutionPayloadV4)including, [], Keccak.Zero, [], [list]);
        ResultWrapper<PayloadStatusV2> omittingStatus = await rpc.engine_newPayloadV7((ExecutionPayloadV4)omitting, [], Keccak.Zero, [], [list]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(including.Transactions, Is.EqualTo(list.Transactions));
            Assert.That(including.StarkProof, Is.EqualTo(depsHash.ToByteArray()), "the block's proof covers the listed transaction's dependency");
            Assert.That(includingStatus.Data.Status, Is.EqualTo(PayloadStatus.Valid), includingStatus.Result.Error);
            Assert.That(includingStatus.Data.InclusionListSatisfied, Is.True);
            Assert.That(omitting.Transactions, Is.Empty);
            Assert.That(omittingStatus.Data.Status, Is.EqualTo(PayloadStatus.Valid), omittingStatus.Result.Error);
            Assert.That(omittingStatus.Data.InclusionListSatisfied, Is.EqualTo(!validListProof),
                validListProof ? "a verified covering proof obligates the listed transaction" : "an invalid proof obligates no dependency-bearing entry");
        }
    }

    // A held proof that does not verify under the target fork is never attached, so the dependency-bearing transaction it would
    // cover stays out of the list and the proof is empty.
    [Test]
    public async Task GetInclusionListV2_leaves_out_dependency_bearing_transactions_without_a_verified_held_proof()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new DepsHashProofVerifier()));
        FrameDependency dependency = new(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("il-unproven"), default);
        Transaction transaction = ExecutableDependencyTransaction(chain.SpecProvider.ChainId, dependency);
        byte[] wrapper = MempoolWrapperDecoder.Instance.Encode(new MempoolWrapper
        {
            Transactions = [transaction],
            Deps = [dependency],
            Mode = MempoolWrapper.ModeDirect,
            Proofs = [[1]]
        }).Bytes;
        Assert.That((await chain.Container.Resolve<ProofWrapperService>().AcceptDetailedAsync(wrapper)).Result.IsSuccess, Is.True);
        chain.Container.Resolve<LeanProofStore>().AddCachedRecursive([dependency], [0xee]);

        using ResultWrapper<GetInclusionListV2Result> listed = await chain.EngineRpcModule.engine_getInclusionListV2();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(listed.Data.Transactions, Is.Empty);
            Assert.That(listed.Data.RecursiveStark.StarkProof, Is.Empty);
            Assert.That(listed.Data.RecursiveStark.ProofDeps, Is.Empty);
        }
    }

    private async Task<ExecutionPayloadV3> BuildPayload(MergeTestBlockchain chain, Hash256 parent, InclusionListV1[] lists, int minTransactions = 0)
    {
        Task improved = chain.WaitForImprovedBlock(parent, minTransactions);
        ResultWrapper<ForkchoiceUpdatedV2Result> build = await chain.EngineRpcModule.engine_forkchoiceUpdatedV6(new(parent, Keccak.Zero, parent),
            BuildPerListPayloadAttributes(lists));
        Assert.That(build.Result.ResultType, Is.EqualTo(ResultType.Success), build.Result.Error);
        await improved;
        return (await chain.EngineRpcModule.engine_getPayloadV6(Bytes.FromHexString(build.Data.PayloadId!))).Data!.ExecutionPayload;
    }

    [Test]
    public async Task GetInclusionListV2_is_refused_without_eip8288()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        using ResultWrapper<GetInclusionListV2Result> result = await chain.EngineRpcModule.engine_getInclusionListV2();
        Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
    }

    private static InclusionListV1 InclusionList(Transaction transaction, RecursiveStarkV1? proof) => new()
    {
        Transactions = [TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes],
        RecursiveStark = proof
    };

    /// <summary>A proof over the transaction's dependencies and a surplus one, in canonical order.</summary>
    private static RecursiveStarkV1 Proof(byte[] starkProof, Transaction transaction) => new()
    {
        StarkProof = starkProof,
        ProofDeps = [.. Eip8288Dependencies.Canonicalize([.. Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue),
                new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("surplus"), default)])
            .Select(static dependency => Eip8288Dependencies.Serialize([dependency]))]
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

    /// <summary>A funded, signed frame transaction whose only dependency is <paramref name="dependency"/>.</summary>
    private static Transaction ExecutableDependencyTransaction(ulong chainId, FrameDependency dependency)
    {
        TxFrame[] frames =
        [
            new(FrameMode.Verify, FrameFlags.ApproveExecutionAndPayment, null, executionGasLimit: FrameTxTestFrames.PrefixFrameGas,
                stateGasLimit: 0, UInt256.Zero, default),
            new(FrameMode.DepVerify, FrameFlags.None, null, Eip8288Constants.LeanSphincsVerificationGas, UInt256.Zero,
                Eip8288Dependencies.Serialize([dependency]))
        ];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = chainId,
            SenderAddress = TestItem.PrivateKeyB.Address,
            NonceKeys = [UInt256.Zero],
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }

    /// <summary>A verifier whose only valid proof of a dependency set is its <c>get_deps_hash</c>.</summary>
    private sealed class DepsHashProofVerifier : ILeanProofVerifier
    {
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) =>
            proof.SequenceEqual(depsHash.Bytes);
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) =>
            depsHash.ToByteArray();
    }

    private sealed class EngineListProofVerifier : ILeanProofVerifier
    {
        public void EnsureAvailable() { }
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => proof is [1];
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) => [1];
    }
}
