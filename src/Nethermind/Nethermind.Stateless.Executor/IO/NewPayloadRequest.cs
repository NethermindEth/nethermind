// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

[SszContainer]
public partial class NewPayloadRequest<TExecutionPayload>
    where TExecutionPayload : SszExecutionPayloadV1, ISszExecutionPayloadFactory<TExecutionPayload>, ISszCodec<TExecutionPayload>, new()
{
    public TExecutionPayload ExecutionPayload { get; set; } = default!;

    [SszList(0x1000)]
    public Hash256[] VersionedHashes { get; set; } = [];

    public Hash256 ParentBeaconBlockRoot { get; set; } = null!;

    public SszExecutionRequests ExecutionRequests { get; set; }

    public static NewPayloadRequest<TExecutionPayload> From(Block block)
    {
        TExecutionPayload payload = TExecutionPayload.From(block);
        Hash256 parentBeaconBlockRoot = block.ParentBeaconBlockRoot
            ?? throw new ArgumentException("Parent beacon block root is missing.", nameof(block));

        List<Hash256> versionedHashes = [];

        foreach (Transaction tx in block.Transactions)
        {
            if (tx.BlobVersionedHashes is null)
                continue;

            foreach (byte[]? hash in tx.BlobVersionedHashes)
            {
                if (hash is not null)
                    versionedHashes.Add(new(hash));
            }
        }

        NewPayloadRequest<TExecutionPayload> request = new()
        {
            ExecutionPayload = payload,
            VersionedHashes = [.. versionedHashes],
            ParentBeaconBlockRoot = parentBeaconBlockRoot
        };

        if (block.ExecutionRequests is null)
        {
            request.ExecutionRequests = new()
            {
                Deposits = [],
                Withdrawals = [],
                Consolidations = [],
                BuilderDeposits = [],
                BuilderExits = []
            };
        }
        else
        {
            (ExecutionRequest[] deposits, ExecutionRequest[] withdrawals, ExecutionRequest[] consolidations,
                ExecutionRequest[] builderDeposits, ExecutionRequest[] builderExits)
                = ExecutionRequestExtensions.GetFlatDecodedRequests(block.ExecutionRequests);

            DepositRequest[] depositRequests = new DepositRequest[deposits.Length];
            WithdrawalRequest[] withdrawalRequests = new WithdrawalRequest[withdrawals.Length];
            ConsolidationRequest[] consolidationRequests = new ConsolidationRequest[consolidations.Length];
            BuilderDepositRequest[] builderDepositRequests = new BuilderDepositRequest[builderDeposits.Length];
            BuilderExitRequest[] builderExitRequests = new BuilderExitRequest[builderExits.Length];

            for (int i = 0; i < deposits.Length; i++)
                depositRequests[i] = DepositRequest.From(deposits[i]);

            for (int i = 0; i < withdrawals.Length; i++)
                withdrawalRequests[i] = WithdrawalRequest.From(withdrawals[i]);

            for (int i = 0; i < consolidations.Length; i++)
                consolidationRequests[i] = ConsolidationRequest.From(consolidations[i]);

            for (int i = 0; i < builderDeposits.Length; i++)
                builderDepositRequests[i] = BuilderDepositRequest.From(builderDeposits[i]);

            for (int i = 0; i < builderExits.Length; i++)
                builderExitRequests[i] = BuilderExitRequest.From(builderExits[i]);

            request.ExecutionRequests = new()
            {
                Deposits = depositRequests,
                Withdrawals = withdrawalRequests,
                Consolidations = consolidationRequests,
                BuilderDeposits = builderDepositRequests,
                BuilderExits = builderExitRequests
            };
        }

        return request;
    }

    public Block? ToBlock()
    {
        if (ExecutionPayload is null)
            return null;

        ExecutionPayload payload = ExecutionPayload.AsExecutionPayload();
        payload.ParentBeaconBlockRoot = ParentBeaconBlockRoot;

        ExecutionRequest[] deposits = new ExecutionRequest[ExecutionRequests.Deposits.Length];

        for (int i = 0; i < deposits.Length; i++)
            deposits[i] = ExecutionRequests.Deposits[i].ToExecutionRequest();

        ExecutionRequest[] withdrawals = new ExecutionRequest[ExecutionRequests.Withdrawals.Length];

        for (int i = 0; i < withdrawals.Length; i++)
            withdrawals[i] = ExecutionRequests.Withdrawals[i].ToExecutionRequest();

        ExecutionRequest[] consolidations = new ExecutionRequest[ExecutionRequests.Consolidations.Length];

        for (int i = 0; i < consolidations.Length; i++)
            consolidations[i] = ExecutionRequests.Consolidations[i].ToExecutionRequest();

        ExecutionRequest[] builderDeposits = new ExecutionRequest[ExecutionRequests.BuilderDeposits.Length];

        for (int i = 0; i < builderDeposits.Length; i++)
            builderDeposits[i] = ExecutionRequests.BuilderDeposits[i].ToExecutionRequest();

        ExecutionRequest[] builderExits = new ExecutionRequest[ExecutionRequests.BuilderExits.Length];

        for (int i = 0; i < builderExits.Length; i++)
            builderExits[i] = ExecutionRequests.BuilderExits[i].ToExecutionRequest();

        using ArrayPoolList<byte[]> pool = ExecutionRequestExtensions.GetFlatEncodedRequests(
            deposits, withdrawals, consolidations, builderDeposits, builderExits);

        payload.ExecutionRequests = [.. pool];

        Result<Block> result = payload.TryGetBlock();

        return result.Data ?? throw new InvalidOperationException(result.Error);
    }
}
