// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Threading;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Merge.Plugin.Test;

public partial class EngineModuleTests
{
    [Test]
    public async Task NewPayloadV3_checks_parameters_inside_the_preparation_the_handler_builds_the_block_with()
    {
        (IEngineRpcModule rpcModule, string? payloadId, Transaction[] transactions, MergeTestBlockchain chain) = await BuildAndGetPayloadV3Result(Cancun.Instance, 1);
        using MergeTestBlockchain disposeChain = chain;
        ExecutionPayloadV3 produced = (await rpcModule.engine_getPayloadV3(Bytes.FromHexString(payloadId!))).Data!.ExecutionPayload;
        GroupTrackingPayloadV3 payload = GroupTrackingPayloadV3.Create(produced.TryGetBlock().Data!);
        // Encoded only, so the parameter checks decode them as they do for a payload read off the wire.
        payload.Transactions = produced.Transactions;
        Hash256[] blobVersionedHashes = transactions.SelectMany(static tx => tx.BlobVersionedHashes ?? []).Select(static h => new Hash256(h!)).ToArray();

        ResultWrapper<PayloadStatusV1> result = await rpcModule.engine_newPayloadV3(payload, blobVersionedHashes, payload.ParentBeaconBlockRoot);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.Data.Status, Is.EqualTo(PayloadStatus.Valid));
            Assert.That(payload.CheckingGroup, Is.Not.Null, "the parameter checks ran outside a preparation");
            Assert.That(payload.BuildingGroup, Is.SameAs(payload.CheckingGroup), "the handler built the block in a preparation of its own");
        }
    }

    /// <summary>Records the worker group current while the parameter checks read the payload and while the handler builds the block.</summary>
    private sealed class GroupTrackingPayloadV3 : ExecutionPayloadV3
    {
        public ParallelUnbalancedWork.WorkerGroup? CheckingGroup { get; private set; }
        public ParallelUnbalancedWork.WorkerGroup? BuildingGroup { get; private set; }

        public new static GroupTrackingPayloadV3 Create(Block block) => Create<GroupTrackingPayloadV3>(block);

        // Of everything engine_newPayloadV3 reaches, only the parameter checks read this.
        public override byte[]? BlockAccessList
        {
            get
            {
                CheckingGroup ??= ParallelUnbalancedWork.GetCurrentGroup();
                return base.BlockAccessList;
            }
            set => base.BlockAccessList = value;
        }

        public override Result<Block> TryGetBlock(UInt256? totalDifficulty = null)
        {
            BuildingGroup ??= ParallelUnbalancedWork.GetCurrentGroup();
            return base.TryGetBlock(totalDifficulty);
        }
    }
}
