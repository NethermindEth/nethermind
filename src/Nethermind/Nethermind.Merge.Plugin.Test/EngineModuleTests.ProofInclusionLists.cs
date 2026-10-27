// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autofac;
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
    // EIPs#12475: a list's commitment never affects block validity. A null commitment obligates no dependency-bearing entry; a
    // committed list obligates it whether or not this node holds the proof, and this entry, lacking a payment frame, is never
    // appendable, so every scenario is satisfied. A malformed entry or unheld proof is scoped to its list, not the request.
    [TestCase("null-commitment")]
    [TestCase("unheld-proof")]
    [TestCase("empty-transaction")]
    public async Task NewPayload_bad_inclusion_proof_does_not_invalidate_the_block(string scenario)
    {
        OverridableReleaseSpec spec = InclusionProofEngineSpec();
        using MergeTestBlockchain chain = await CreateBlockchain(spec, new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        Transaction transaction = InclusionDependencyTransaction(chain.SpecProvider.ChainId);
        InclusionListV1[] lists = [InclusionList(3, transaction, scenario is "null-commitment" or "empty-transaction" ? null : Commitment([2]))];
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

    // The execution-apis style InclusionListV1 wire form: committeeIndex, transactions and recursiveStark (null for a null commitment).
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
        string lists = $$$"""[{"committeeIndex":"{{{committeeIndex}}}","transactions":["{{{encoded}}}"],"recursiveStark":{"starkProofHash":"{{{TestItem.KeccakA}}}","depsHash":"{{{TestItem.KeccakB}}}"}}]""";

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
            "oversized-list" => [new() { Transactions = [new byte[Eip7805Constants.MaxBytesPerInclusionList + 1]] }],
            _ => [.. Enumerable.Range(0, Eip7805Constants.InclusionListCommitteeSize + 1).Select(i => new InclusionListV1 { CommitteeIndex = (ulong)i, Transactions = [] })]
        };

        ResultWrapper<PayloadStatusV2> result = await rpc.engine_newPayloadV7(produced.ExecutionPayload, [], Keccak.Zero, produced.ExecutionRequests, lists);

        Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams));
    }

    [TestCase("missing-proof")]
    [TestCase("bad-proof")]
    [TestCase("incomplete-commitment")]
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
        RecursiveStarkV1? commitment = scenario switch
        {
            "missing-proof" => null,
            "incomplete-commitment" => new RecursiveStarkV1 { StarkProofHash = TestItem.KeccakA },
            _ => Commitment([2])
        };
        PayloadAttributes attributes = BuildPerListPayloadAttributes([InclusionList(0, transaction, commitment)],
            timestamp: produced.ExecutionPayload.Timestamp + 12, slotNumber: 2);
        Hash256 head = produced.ExecutionPayload.BlockHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> result = await rpc.engine_forkchoiceUpdatedV6(new(head, head, head), attributes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.HeadHash, Is.EqualTo(head));
            Assert.That(chain.BlockTree.SafeHash, Is.EqualTo(head));
            Assert.That(chain.BlockTree.FinalizedHash, Is.EqualTo(head));
            Assert.That(result.Result.ResultType, Is.EqualTo(scenario == "incomplete-commitment" ? ResultType.Failure : ResultType.Success));
            if (scenario == "incomplete-commitment") Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidPayloadAttributes));
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

    private static InclusionListV1 InclusionList(ulong committeeIndex, Transaction transaction, RecursiveStarkV1? commitment) => new()
    {
        CommitteeIndex = committeeIndex,
        Transactions = [TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes],
        RecursiveStark = commitment
    };

    /// <summary>The next slot's target block timestamp; these chains run one fork throughout.</summary>
    private static ulong ProofTimestamp => (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 12;

    /// <summary>A consensus-layer attestation deadline ahead of now, in Unix milliseconds.</summary>
    private static ulong AttestationDeadlineMs => (ProofTimestamp + 4) * 1000;

    /// <summary>A consensus-layer view freeze deadline ahead of now, in Unix milliseconds.</summary>
    private static ulong ViewFreezeDeadlineMs => (ProofTimestamp - 3) * 1000;

    private static RecursiveStarkV1 Commitment(byte[] proof, Hash256? depsHash = null) =>
        new() { StarkProofHash = Keccak.Compute(proof), DepsHash = depsHash ?? TestItem.KeccakB };

    // EIPs#12475 Test Case 9: attesters check a commitment through the EL before counting the list. A commitment to a proof
    // whose deps strictly contain the list's is VALID once held, PROOF_UNKNOWN until the il_proof is supplied, and an il_proof that
    // does not match the commitment is ignored. execution-apis#945: an il_proof over its bounds is INVALID when it matches and
    // ignored otherwise, never a JSON-RPC error; only a malformed parameter, such as a proofDeps entry that is not 96 bytes, is.
    [TestCase("il-proof", PayloadStatus.Valid, null)]
    [TestCase("held-after-il-proof", PayloadStatus.Valid, null)]
    [TestCase("not-held", InclusionListProofStatusV1.ProofUnknownStatus, null)]
    [TestCase("mismatched-il-proof", InclusionListProofStatusV1.ProofUnknownStatus, null)]
    [TestCase("invalid-proof", PayloadStatus.Invalid, null)]
    [TestCase("undecodable-entry-ignored", PayloadStatus.Valid, null)]
    [TestCase("null-commitment", null, ErrorCodes.InvalidParams)]
    [TestCase("incomplete-commitment", null, ErrorCodes.InvalidParams)]
    [TestCase("malformed-il-proof", null, ErrorCodes.InvalidParams)]
    [TestCase("oversized-matching-il-proof", PayloadStatus.Invalid, null)]
    [TestCase("oversized-mismatched-il-proof", InclusionListProofStatusV1.ProofUnknownStatus, null)]
    [TestCase("oversized-list", null, ErrorCodes.InvalidParams)]
    public async Task VerifyInclusionListProofV1_checks_the_commitment_against_the_list(string scenario, string? expectedStatus, int? expectedError)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        Transaction transaction = InclusionDependencyTransaction(chain.SpecProvider.ChainId);
        byte[] encoded = TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes;
        List<FrameDependency> proofDeps = Eip8288Dependencies.Canonicalize([.. Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue),
            new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("surplus"), default)]);
        byte[] proof = scenario switch
        {
            "invalid-proof" => [2],
            "oversized-matching-il-proof" or "oversized-mismatched-il-proof" => new byte[Eip8288Constants.MaxAggregateProofBytes + 1],
            _ => [1]
        };
        RecursiveStarkV1? commitment = scenario switch
        {
            "null-commitment" => null,
            "incomplete-commitment" => new RecursiveStarkV1 { StarkProofHash = Keccak.Compute(proof) },
            "oversized-mismatched-il-proof" => Commitment([1], new Hash256(Eip8288Dependencies.ComputeDepsHash(proofDeps))),
            _ => Commitment(proof, new Hash256(Eip8288Dependencies.ComputeDepsHash(proofDeps)))
        };
        InclusionListProofV1 ilProof = new()
        {
            StarkProof = proof,
            ProofDeps = scenario switch
            {
                "malformed-il-proof" => [[1]],
                "mismatched-il-proof" => [.. proofDeps.Take(1).Select(static d => Eip8288Dependencies.Serialize([d]))],
                _ => [.. proofDeps.Select(static d => Eip8288Dependencies.Serialize([d]))]
            }
        };
        byte[][] transactions = scenario switch
        {
            "undecodable-entry-ignored" => [encoded, [0xc0]],
            "oversized-list" => [new byte[Eip7805Constants.MaxBytesPerInclusionList + 1]],
            _ => [encoded]
        };
        if (scenario == "held-after-il-proof")
            Assert.That(chain.EngineRpcModule.engine_verifyInclusionListProofV1(ProofTimestamp, transactions, commitment!, ilProof, AttestationDeadlineMs).Data.Status,
                Is.EqualTo(PayloadStatus.Valid));

        ResultWrapper<InclusionListProofStatusV1> result = chain.EngineRpcModule.engine_verifyInclusionListProofV1(ProofTimestamp, transactions, commitment!,
            scenario is "not-held" or "held-after-il-proof" ? null : ilProof, AttestationDeadlineMs);

        using (Assert.EnterMultipleScope())
        {
            if (expectedError is { } code)
            {
                Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Failure));
                Assert.That(result.ErrorCode, Is.EqualTo(code));
            }
            else
            {
                Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
                Assert.That(result.Data.Status, Is.EqualTo(expectedStatus));
                Assert.That(result.Data.ValidationError is null, Is.EqualTo(expectedStatus != PayloadStatus.Invalid));
            }
        }
    }

    // EIPs#12475: a node serves the il_proof it holds under a stark_proof_hash, from a validated mode-1 wrapper or a verified
    // il_proof, so the consensus layer can gossip or fetch it; an unknown hash returns null.
    [Test]
    public async Task GetInclusionListProofV1_serves_held_proofs_with_their_dependencies()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        LeanProofStore store = chain.Container.Resolve<LeanProofStore>();
        List<FrameDependency> wrapperDeps = Eip8288Dependencies.Canonicalize([
            new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("a"), default),
            new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("b"), default)]);
        store.AddVerified(wrapperDeps, null, [1]);

        ResultWrapper<InclusionListProofV1?> held = chain.EngineRpcModule.engine_getInclusionListProofV1(ProofTimestamp, Keccak.Compute([1]));
        ResultWrapper<InclusionListProofV1?> unknown = chain.EngineRpcModule.engine_getInclusionListProofV1(ProofTimestamp, Keccak.Compute([9]));
        ResultWrapper<InclusionListProofV1?> missing = chain.EngineRpcModule.engine_getInclusionListProofV1(ProofTimestamp, null!);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(held.Data!.StarkProof, Is.EqualTo(new byte[] { 1 }));
            Assert.That(held.Data.ProofDeps, Is.EqualTo(wrapperDeps.Select(static d => Eip8288Dependencies.Serialize([d]))));
            Assert.That(unknown.Result.ResultType, Is.EqualTo(ResultType.Success));
            Assert.That(unknown.Data, Is.Null);
            Assert.That(missing.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams), "a null starkProofHash is an invalid parameter");
        }
    }

    // engine_submitInclusionListProofV1 verifies a proof-topic il_proof and records its verdict, so a later list committing to it
    // is VALID by lookup; proofs over the bounds or with noncanonical proof_deps are INVALID before verification.
    [TestCase("valid", PayloadStatus.Valid)]
    [TestCase("invalid-proof", PayloadStatus.Invalid)]
    [TestCase("unsorted-deps", PayloadStatus.Invalid)]
    [TestCase("oversized-proof", PayloadStatus.Invalid)]
    [TestCase("too-many-deps", PayloadStatus.Invalid)]
    [TestCase("disabled-scheme", PayloadStatus.Invalid)]
    [TestCase("short-dependency-entry", null)]
    public async Task SubmitInclusionListProofV1_records_topic_proofs(string scenario, string? expectedStatus)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        Transaction transaction = InclusionDependencyTransaction(chain.SpecProvider.ChainId);
        List<FrameDependency> proofDeps = Eip8288Dependencies.Canonicalize([.. Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue),
            new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("surplus"), default)]);
        byte[] proof = scenario switch
        {
            "invalid-proof" => [2],
            "oversized-proof" => new byte[Eip8288Constants.MaxAggregateProofBytes + 1],
            _ => [1]
        };
        IEnumerable<FrameDependency> ordered = scenario switch
        {
            "unsorted-deps" => proofDeps.AsEnumerable().Reverse(),
            "too-many-deps" => Eip8288Dependencies.Canonicalize(Enumerable.Range(0, Eip8288Constants.MaxDepsPerAggregateWrapper + 1).Select(static i =>
                new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute(BitConverter.GetBytes(i)), default))),
            "disabled-scheme" => [.. proofDeps, new FrameDependency(0x7f, ValueKeccak.Compute("disabled"), default)],
            _ => proofDeps
        };
        InclusionListProofV1 ilProof = new() { StarkProof = proof, ProofDeps = [.. ordered.Select(static d => Eip8288Dependencies.Serialize([d]))] };
        if (scenario == "short-dependency-entry") ilProof.ProofDeps![0] = ilProof.ProofDeps[0][..95];

        ResultWrapper<InclusionListProofStatusV1> result = chain.EngineRpcModule.engine_submitInclusionListProofV1(ProofTimestamp, ilProof, ViewFreezeDeadlineMs);

        using (Assert.EnterMultipleScope())
        {
            if (expectedStatus is null)
            {
                Assert.That(result.ErrorCode, Is.EqualTo(ErrorCodes.InvalidParams), "a proofDeps entry is bytes96");
                return;
            }
            Assert.That(result.Data.Status, Is.EqualTo(expectedStatus), result.Data.ValidationError);
            if (scenario == "valid")
            {
                byte[][] list = [TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes];
                Assert.That(chain.EngineRpcModule.engine_verifyInclusionListProofV1(ProofTimestamp, list,
                    Commitment(proof, new Hash256(Eip8288Dependencies.ComputeDepsHash(proofDeps))), null, AttestationDeadlineMs).Data.Status,
                    Is.EqualTo(PayloadStatus.Valid));
                Assert.That(chain.EngineRpcModule.engine_getInclusionListProofV1(ProofTimestamp, Keccak.Compute(proof)).Data!.StarkProof,
                    Is.EqualTo(proof));
            }
        }
    }

    // execution-apis#945 shapes: positional (timestamp, transactions, recursiveStark, ilProof | null, attestationDeadlineMs) and
    // (timestamp, ilProof, viewFreezeDeadlineMs) with QUANTITY deadlines in Unix milliseconds, the RecursiveStarkV1 and
    // InclusionListProofV1 field names, and an InclusionListProofStatusV1 that always carries validationError.
    [TestCase("verify-without-dependencies", "\"status\":\"INVALID\",\"validationError\":\"")]
    [TestCase("verify-not-held", "{\"status\":\"PROOF_UNKNOWN\",\"validationError\":null}")]
    [TestCase("submit-unsorted", "\"status\":\"INVALID\",\"validationError\":\"")]
    [TestCase("submit-valid", "{\"status\":\"VALID\",\"validationError\":null}")]
    public async Task Inclusion_list_proof_methods_read_and_write_execution_apis_json(string scenario, string expected)
    {
        using MergeTestBlockchain chain = await CreateBlockchain(InclusionProofEngineSpec(), new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        Transaction transaction = InclusionDependencyTransaction(chain.SpecProvider.ChainId);
        List<FrameDependency> proofDeps = Eip8288Dependencies.ForTransaction(transaction, FrameTxSigHash.ComputeValue);
        proofDeps = Eip8288Dependencies.Canonicalize([.. proofDeps, new FrameDependency(Eip8288Constants.LeanSphincsScheme, ValueKeccak.Compute("surplus"), default)]);
        IEnumerable<FrameDependency> ordered = scenario == "submit-unsorted" ? proofDeps.AsEnumerable().Reverse() : proofDeps;
        string deps = string.Join(",", ordered.Select(static d => $"\"{Eip8288Dependencies.Serialize([d]).ToHexString(true)}\""));
        string ilProof = $$"""{"starkProof":"0x01","proofDeps":[{{deps}}]}""";
        string timestamp = $"0x{ProofTimestamp:x}";
        string list = scenario == "verify-without-dependencies"
            ? $"[\"{TxDecoder.Instance.Encode(Build.A.Transaction.SignedAndResolved().TestObject, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true)}\"]"
            : $"[\"{TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes.ToHexString(true)}\"]";
        string commitment = $$"""{"starkProofHash":"{{Keccak.Compute([1])}}","depsHash":"{{TestItem.KeccakB}}"}""";

        string response = scenario.StartsWith("verify")
            ? await RpcTest.TestSerializedRequest(chain.EngineRpcModule, "engine_verifyInclusionListProofV1", timestamp,
                JsonDocument.Parse(list).RootElement, JsonDocument.Parse(commitment).RootElement, null, $"0x{AttestationDeadlineMs:x}")
            : await RpcTest.TestSerializedRequest(chain.EngineRpcModule, "engine_submitInclusionListProofV1", timestamp,
                JsonDocument.Parse(ilProof).RootElement, $"0x{ViewFreezeDeadlineMs:x}");

        Assert.That(response, Does.Contain(expected));
    }

    [Test]
    public async Task VerifyInclusionListProofV1_is_refused_without_eip8288()
    {
        using MergeTestBlockchain chain = await CreateBlockchain(Bogota.Instance, new MergeConfig { TerminalTotalDifficulty = "0" });
        ResultWrapper<InclusionListProofStatusV1> result = chain.EngineRpcModule.engine_verifyInclusionListProofV1(ProofTimestamp, [], Commitment([1]), null,
            AttestationDeadlineMs);
        Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
        Assert.That(chain.EngineRpcModule.engine_getInclusionListProofV1(ProofTimestamp, Keccak.Compute([1])).ErrorCode, Is.EqualTo(MergeErrorCodes.UnsupportedFork));
        Assert.That(chain.EngineRpcModule.engine_submitInclusionListProofV1(ProofTimestamp, new InclusionListProofV1(), ViewFreezeDeadlineMs).ErrorCode,
            Is.EqualTo(MergeErrorCodes.UnsupportedFork));
    }

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
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => proof is [1];
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) => [1];
    }
}
