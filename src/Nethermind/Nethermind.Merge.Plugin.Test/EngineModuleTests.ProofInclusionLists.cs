// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Consensus.Producers;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [TestCase("missing-proof")]
    [TestCase("bad-proof")]
    [TestCase("wrong-commitment")]
    public async Task NewPayload_bad_inclusion_proof_does_not_invalidate_the_block(string scenario)
    {
        OverridableReleaseSpec spec = new(Bogota.Instance) { IsEip8141Enabled = true, IsEip8288Enabled = true };
        using MergeTestBlockchain chain = await CreateBlockchain(spec, new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        ResultWrapper<ForkchoiceUpdatedV2Result> build = await rpc.engine_forkchoiceUpdatedV5(
            new(parent, Keccak.Zero, parent), BuildBogotaPayloadAttributes([]));
        Assert.That(build.Result.ResultType, Is.EqualTo(ResultType.Success), build.Result.Error);
        GetPayloadV6Result produced = (await rpc.engine_getPayloadV6(Bytes.FromHexString(build.Data.PayloadId!))).Data!;
        Transaction transaction = InclusionDependencyTransaction(chain.SpecProvider.ChainId);
        ValueHash256 depsHash = Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.ForTransaction(transaction));
        produced.ExecutionPayload.InclusionListRecursiveStark = scenario == "missing-proof" ? null
            : new([2], scenario == "wrong-commitment" ? Keccak.Zero : new Hash256(depsHash));

        ResultWrapper<PayloadStatusV2> result = await rpc.engine_newPayloadV6(produced.ExecutionPayload,
            [], Keccak.Zero, produced.ExecutionRequests, [TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes]);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), result.Result.Error);
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(result.Data.InclusionListSatisfied, Is.False);
            Assert.That(result.Data.LatestValidHash, Is.EqualTo(produced.ExecutionPayload.BlockHash));
        }
    }

    [TestCase("missing-proof")]
    [TestCase("bad-proof")]
    [TestCase("empty-proof")]
    public async Task Forkchoice_applies_checkpoints_before_inclusion_proof_attributes(string scenario)
    {
        OverridableReleaseSpec spec = new(Bogota.Instance) { IsEip8141Enabled = true, IsEip8288Enabled = true };
        using MergeTestBlockchain chain = await CreateBlockchain(spec, new MergeConfig { TerminalTotalDifficulty = "0" },
            configurer: builder => builder.AddSingleton<ILeanProofVerifier>(new EngineListProofVerifier()));
        IEngineRpcModule rpc = chain.EngineRpcModule;
        Hash256 parent = chain.BlockTree.HeadHash!;
        ResultWrapper<ForkchoiceUpdatedV2Result> build = await rpc.engine_forkchoiceUpdatedV5(
            new(parent, Keccak.Zero, parent), BuildBogotaPayloadAttributes([]));
        GetPayloadV6Result produced = (await rpc.engine_getPayloadV6(Bytes.FromHexString(build.Data.PayloadId!))).Data!;
        ResultWrapper<PayloadStatusV2> imported = await rpc.engine_newPayloadV6(produced.ExecutionPayload, [], Keccak.Zero, produced.ExecutionRequests, []);
        Assert.That(imported.Data.Status, Is.EqualTo(PayloadStatus.Valid));
        Transaction transaction = InclusionDependencyTransaction(chain.SpecProvider.ChainId);
        PayloadAttributes attributes = BuildBogotaPayloadAttributes([TxDecoder.Instance.Encode(transaction, RlpBehaviors.SkipTypedWrapping).Bytes],
            timestamp: produced.ExecutionPayload.Timestamp + 12, slotNumber: 2);
        attributes.InclusionListRecursiveStark = scenario == "missing-proof" ? null
            : new(scenario == "empty-proof" ? [] : [2], new Hash256(Eip8288Dependencies.ComputeDepsHash(Eip8288Dependencies.ForTransaction(transaction))));
        Hash256 head = produced.ExecutionPayload.BlockHash;

        ResultWrapper<ForkchoiceUpdatedV2Result> result = await rpc.engine_forkchoiceUpdatedV5(new(head, head, head), attributes);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(chain.BlockTree.HeadHash, Is.EqualTo(head));
            Assert.That(chain.BlockTree.SafeHash, Is.EqualTo(head));
            Assert.That(chain.BlockTree.FinalizedHash, Is.EqualTo(head));
            Assert.That(result.Result.ResultType, Is.EqualTo(scenario == "empty-proof" ? ResultType.Failure : ResultType.Success));
            if (scenario == "empty-proof") Assert.That(result.ErrorCode, Is.EqualTo(MergeErrorCodes.InvalidPayloadAttributes));
        }
    }

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
            Frames = frames,
            FrameSignatures = [],
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei
        };
    }

    private sealed class EngineListProofVerifier : ILeanProofVerifier
    {
        public bool VerifyLeanSphincs(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyLeanStark(in ValueHash256 dataHash, in ValueHash256 verificationKey, ReadOnlySpan<byte> witness) => true;
        public bool VerifyRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, ReadOnlySpan<byte> proof) => proof is [1];
        public byte[] ProveRecursiveStark(in ValueHash256 depsHash, ReadOnlySpan<byte> aggregatedVk, AggregationInput input) => [1];
    }
}
