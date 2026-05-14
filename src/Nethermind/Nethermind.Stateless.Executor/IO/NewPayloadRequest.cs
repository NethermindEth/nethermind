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
    where TExecutionPayload : SszExecutionPayloadV1, ISszCodec<TExecutionPayload>, new()
{
    public TExecutionPayload ExecutionPayload { get; set; } = default!;

    [SszList(0x1000)]
    public Hash256[] VersionedHashes { get; set; } = [];

    public Hash256 ParentBeaconBlockRoot { get; set; } = null!;

    public SszExecutionRequests ExecutionRequests { get; set; }

    public static NewPayloadRequest<TExecutionPayload> From(Block block)
    {
        SszExecutionPayloadV1 payload = typeof(TExecutionPayload) switch
        {
            Type t when t == typeof(SszExecutionPayloadV4) => new SszExecutionPayloadV4(block),
            Type t when t == typeof(SszExecutionPayloadV3) => new SszExecutionPayloadV3(block),
            Type t when t == typeof(SszExecutionPayloadV2) => new SszExecutionPayloadV2(block),
            _ => new SszExecutionPayloadV1(block)
        };

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
            ExecutionPayload = (TExecutionPayload)payload,
            VersionedHashes = [.. versionedHashes],
            ParentBeaconBlockRoot = block.ParentBeaconBlockRoot!
        };

        if (block.ExecutionRequests is null)
        {
            request.ExecutionRequests = new()
            {
                Deposits = [],
                Withdrawals = [],
                Consolidations = []
            };
        }
        else
        {
            (ExecutionRequest[] deposits, ExecutionRequest[] withdrawals, ExecutionRequest[] consolidations)
                = ExecutionRequestExtensions.GetFlatDecodedRequests(block.ExecutionRequests);

            DepositRequest[] depositRequests = new DepositRequest[deposits.Length];
            WithdrawalRequest[] withdrawalRequests = new WithdrawalRequest[withdrawals.Length];
            ConsolidationRequest[] consolidationRequests = new ConsolidationRequest[consolidations.Length];

            for (int i = 0; i < deposits.Length; i++)
                depositRequests[i] = DepositRequest.From(deposits[i]);

            for (int i = 0; i < withdrawals.Length; i++)
                withdrawalRequests[i] = WithdrawalRequest.From(withdrawals[i]);

            for (int i = 0; i < consolidations.Length; i++)
                consolidationRequests[i] = ConsolidationRequest.From(consolidations[i]);

            request.ExecutionRequests = new()
            {
                Deposits = depositRequests,
                Withdrawals = withdrawalRequests,
                Consolidations = consolidationRequests
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

        using ArrayPoolList<byte[]> pool = ExecutionRequestExtensions.GetFlatEncodedRequests(
            deposits, withdrawals, consolidations);

        payload.ExecutionRequests = [.. pool];

        BlockDecodingResult result = payload.TryGetBlock();

        return result.Block ?? throw new InvalidOperationException(result.Error);
    }
}
