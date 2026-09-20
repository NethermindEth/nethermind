// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.BeaconChain.Types;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Crypto;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Ssz;

namespace Nethermind.BeaconChain.Engine;

/// <summary>
/// Converts beacon-chain payload containers into the <c>engine_newPayloadV4</c> (Fulu block body)
/// and <c>engine_newPayloadV5</c> (Gloas execution payload envelope) parameter shapes.
/// </summary>
public static class PayloadConverter
{
    /// <summary>Maps the beacon <see cref="Types.ExecutionPayload"/> field-by-field onto the engine API <see cref="ExecutionPayloadV3"/>.</summary>
    public static ExecutionPayloadV3 ToExecutionPayloadV3(Types.ExecutionPayload payload)
    {
        Types.Transaction[] transactions = payload.Transactions ?? [];
        byte[][] encodedTransactions = new byte[transactions.Length][];
        for (int i = 0; i < transactions.Length; i++)
        {
            encodedTransactions[i] = transactions[i].Bytes ?? [];
        }

        return new ExecutionPayloadV3
        {
            ParentHash = payload.ParentHash!,
            FeeRecipient = payload.FeeRecipient!,
            StateRoot = payload.StateRoot!,
            ReceiptsRoot = payload.ReceiptsRoot!,
            LogsBloom = payload.LogsBloom!,
            PrevRandao = payload.PrevRandao!,
            BlockNumber = payload.BlockNumber,
            GasLimit = payload.GasLimit,
            GasUsed = payload.GasUsed,
            Timestamp = payload.Timestamp,
            ExtraData = payload.ExtraData ?? [],
            BaseFeePerGas = payload.BaseFeePerGas,
            BlockHash = payload.BlockHash!,
            Transactions = encodedTransactions,
            Withdrawals = ToCoreWithdrawals(payload.Withdrawals),
            BlobGasUsed = payload.BlobGasUsed,
            ExcessBlobGas = payload.ExcessBlobGas,
        };
    }

    /// <summary>
    /// Maps the Gloas <see cref="ExecutionPayloadGloas"/> (an envelope's payload) onto the engine API
    /// <see cref="ExecutionPayloadV4"/>: the V3 fields plus the EIP-7928 block access list, passed
    /// through as the RLP bytes the envelope carries, and the EIP-7843 slot number.
    /// </summary>
    public static ExecutionPayloadV4 ToExecutionPayloadV4(ExecutionPayloadGloas payload)
    {
        TransactionGloas[] transactions = payload.Transactions ?? [];
        byte[][] encodedTransactions = new byte[transactions.Length][];
        for (int i = 0; i < transactions.Length; i++)
        {
            encodedTransactions[i] = transactions[i].Bytes ?? [];
        }

        return new ExecutionPayloadV4
        {
            ParentHash = payload.ParentHash!,
            FeeRecipient = payload.FeeRecipient!,
            StateRoot = payload.StateRoot!,
            ReceiptsRoot = payload.ReceiptsRoot!,
            LogsBloom = payload.LogsBloom!,
            PrevRandao = payload.PrevRandao!,
            BlockNumber = payload.BlockNumber,
            GasLimit = payload.GasLimit,
            GasUsed = payload.GasUsed,
            Timestamp = payload.Timestamp,
            ExtraData = payload.ExtraData ?? [],
            BaseFeePerGas = payload.BaseFeePerGas,
            BlockHash = payload.BlockHash!,
            Transactions = encodedTransactions,
            Withdrawals = ToCoreWithdrawals(payload.Withdrawals),
            BlobGasUsed = payload.BlobGasUsed,
            ExcessBlobGas = payload.ExcessBlobGas,
            BlockAccessList = payload.BlockAccessList ?? [],
            SlotNumber = payload.SlotNumber,
        };
    }

    private static Core.Withdrawal[] ToCoreWithdrawals(Types.Withdrawal[]? withdrawals)
    {
        withdrawals ??= [];
        Core.Withdrawal[] coreWithdrawals = new Core.Withdrawal[withdrawals.Length];
        for (int i = 0; i < withdrawals.Length; i++)
        {
            Types.Withdrawal withdrawal = withdrawals[i];
            coreWithdrawals[i] = new Core.Withdrawal
            {
                Index = withdrawal.Index,
                ValidatorIndex = withdrawal.ValidatorIndex,
                Address = withdrawal.Address!,
                AmountInGwei = withdrawal.Amount,
            };
        }
        return coreWithdrawals;
    }

    /// <summary>The EIP-4844 <c>kzg_to_versioned_hash</c> over each commitment: SHA-256 with the first byte replaced by the version <c>0x01</c>.</summary>
    public static Hash256?[] ToBlobVersionedHashes(SszKzgCommitment[]? commitments)
    {
        if (commitments is null || commitments.Length == 0)
        {
            return [];
        }

        Hash256?[] hashes = new Hash256?[commitments.Length];
        for (int i = 0; i < commitments.Length; i++)
        {
            byte[] hash = new byte[Hash256.Size];
            KzgPolynomialCommitments.TryComputeCommitmentHashV1(commitments[i].AsSpan(), hash);
            hashes[i] = new Hash256(hash);
        }

        return hashes;
    }

    /// <summary>
    /// The Electra <c>get_execution_requests_list</c>: flat EIP-7685 encoding of the requests for
    /// <c>engine_newPayloadV4</c> — one <c>request_type || ssz_serialize(request_data)</c> element
    /// per non-empty list, in ascending request-type order.
    /// </summary>
    public static byte[][] ToExecutionRequestsList(ExecutionRequests? requests)
    {
        if (requests is null)
        {
            return [];
        }

        List<byte[]> list = new(3);
        AppendRequests(list, ExecutionRequestType.Deposit, requests.Deposits);
        AppendRequests(list, ExecutionRequestType.WithdrawalRequest, requests.Withdrawals);
        AppendRequests(list, ExecutionRequestType.ConsolidationRequest, requests.Consolidations);
        return list.ToArray();
    }

    /// <summary>
    /// The Gloas <c>get_execution_requests_list</c> for <c>engine_newPayloadV5</c>: the Electra three
    /// plus the EIP-8282 builder deposit (<c>0x03</c>) and builder exit (<c>0x04</c>) requests, still
    /// one element per non-empty list in ascending request-type order.
    /// </summary>
    public static byte[][] ToExecutionRequestsList(ExecutionRequestsGloas? requests)
    {
        if (requests is null)
        {
            return [];
        }

        List<byte[]> list = new(5);
        AppendRequests(list, ExecutionRequestType.Deposit, requests.Deposits);
        AppendRequests(list, ExecutionRequestType.WithdrawalRequest, requests.Withdrawals);
        AppendRequests(list, ExecutionRequestType.ConsolidationRequest, requests.Consolidations);
        AppendRequests(list, ExecutionRequestType.BuilderDepositRequest, requests.BuilderDeposits);
        AppendRequests(list, ExecutionRequestType.BuilderExitRequest, requests.BuilderExits);
        return list.ToArray();
    }

    private static void AppendRequests<T>(List<byte[]> list, ExecutionRequestType type, T[]? items) where T : ISszCodec<T>
    {
        if (items is null || items.Length == 0)
        {
            return;
        }

        // The request items are fixed-size containers, so the SSZ list serialization is their concatenation.
        byte[] encoded = new byte[1 + T.GetLength(items)];
        encoded[0] = (byte)type;
        T.Encode(encoded.AsSpan(1), items);
        list.Add(encoded);
    }
}
