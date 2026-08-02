// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// Factory contract for SSZ execution payload wrappers.
/// </summary>
/// <typeparam name="TSszExecutionPayload">The concrete SSZ execution payload wrapper type.</typeparam>
public interface ISszExecutionPayloadFactory<out TSszExecutionPayload>
    where TSszExecutionPayload : SszExecutionPayloadV1
{
    /// <summary>
    /// Creates an SSZ execution payload wrapper from an execution block.
    /// </summary>
    /// <param name="block">The block to wrap.</param>
    /// <returns>The concrete SSZ execution payload wrapper.</returns>
    static abstract TSszExecutionPayload From(Block block);
}

/// <summary>
/// SSZ wrapper for ExecutionPayloadV1 (pre-Shanghai / pre-EIP-4895).
/// Using this type for V1 newPayload encode/decode avoids the 4-byte withdrawals offset
/// that <see cref="SszExecutionPayload"/> would include in the fixed header.
/// </summary>
[SszContainer]
public partial class SszExecutionPayloadV1(ExecutionPayload payload) : ISszExecutionPayloadFactory<SszExecutionPayloadV1>
{
    public SszExecutionPayloadV1() : this(new ExecutionPayload()) { }

    /// <inheritdoc/>
    public static SszExecutionPayloadV1 From(Block block) => new(ExecutionPayload.Create(block));

    protected virtual ExecutionPayload Inner { get; private set; } = payload;

    public virtual ExecutionPayload AsExecutionPayload() => Inner;

    public Hash256 ParentHash
    {
        get => Inner.ParentHash;
        set => Inner.ParentHash = value;
    }

    public Address FeeRecipient
    {
        get => Inner.FeeRecipient;
        set => Inner.FeeRecipient = value;
    }

    public Hash256 StateRoot
    {
        get => Inner.StateRoot;
        set => Inner.StateRoot = value;
    }

    public Hash256 ReceiptsRoot
    {
        get => Inner.ReceiptsRoot;
        set => Inner.ReceiptsRoot = value;
    }

    public Bloom LogsBloom
    {
        get => Inner.LogsBloom;
        set => Inner.LogsBloom = value;
    }

    public Hash256 PrevRandao
    {
        get => Inner.PrevRandao;
        set => Inner.PrevRandao = value;
    }

    public ulong BlockNumber
    {
        get => Inner.BlockNumber;
        set => Inner.BlockNumber = value;
    }

    public ulong GasLimit
    {
        get => Inner.GasLimit;
        set => Inner.GasLimit = value;
    }

    public ulong GasUsed
    {
        get => Inner.GasUsed;
        set => Inner.GasUsed = value;
    }

    public ulong Timestamp
    {
        get => Inner.Timestamp;
        set => Inner.Timestamp = value;
    }

    [SszList(32)]
    public byte[] ExtraData
    {
        get => Inner.ExtraData;
        set => Inner.ExtraData = value;
    }

    public UInt256 BaseFeePerGas
    {
        get => Inner.BaseFeePerGas;
        set => Inner.BaseFeePerGas = value;
    }

    public Hash256 BlockHash
    {
        get => Inner.BlockHash;
        set => Inner.BlockHash = value;
    }

    [SszList(0x10_0000)]
    public SszTransaction[] Transactions
    {
        get
        {
            if (field is not null) return field;
            byte[][] txs = Inner.Transactions;
            if (txs.Length == 0) return [];
            field = new SszTransaction[txs.Length];
            for (int i = 0; i < txs.Length; i++)
                field[i] = new SszTransaction { Bytes = txs[i] };
            return field;
        }
        set
        {
            field = value;
            if (value is null || value.Length == 0)
            {
                Inner.Transactions = [];
                return;
            }
            byte[][] raw = new byte[value.Length][];
            for (int i = 0; i < value.Length; i++)
                raw[i] = value[i].Bytes ?? [];
            Inner.Transactions = raw;
        }
    }
}

/// <summary>
/// Thin SSZ wrapper around <see cref="ExecutionPayload"/>.
/// Keeps all SSZ-specific type adaptations out of the domain class.
/// </summary>
[SszContainer]
public partial class SszExecutionPayloadV2(ExecutionPayload payload)
    : SszExecutionPayloadV1(payload), ISszExecutionPayloadFactory<SszExecutionPayloadV2>
{
    public SszExecutionPayloadV2() : this(new ExecutionPayload()) { }

    /// <inheritdoc/>
    public new static SszExecutionPayloadV2 From(Block block) => new(ExecutionPayload.Create(block));

    [SszList(16)]
    public SszWithdrawal[] Withdrawals
    {
        get
        {
            if (field is not null) return field;
            Withdrawal[]? ws = Inner.Withdrawals;
            if (ws is null || ws.Length == 0) return [];
            field = new SszWithdrawal[ws.Length];
            for (int i = 0; i < ws.Length; i++)
                field[i] = new SszWithdrawal
                {
                    Index = ws[i].Index,
                    ValidatorIndex = ws[i].ValidatorIndex,
                    Address = ws[i].Address,
                    Amount = ws[i].AmountInGwei
                };
            return field;
        }
        set
        {
            field = value;

            if (value is null)
            {
                Inner.Withdrawals = null;
                return;
            }

            if (value.Length == 0)
            {
                Inner.Withdrawals = [];
                return;
            }

            Withdrawal[] result = new Withdrawal[value.Length];

            for (int i = 0; i < value.Length; i++)
            {
                result[i] = new Withdrawal
                {
                    Index = value[i].Index,
                    ValidatorIndex = value[i].ValidatorIndex,
                    Address = value[i].Address,
                    AmountInGwei = value[i].Amount
                };
            }

            Inner.Withdrawals = result;
        }
    }
}

/// <summary>
/// Thin SSZ wrapper around <see cref="ExecutionPayloadV3"/>, extending
/// <see cref="SszExecutionPayload"/> with EIP-4844 fields.
/// </summary>
[SszContainer]
public partial class SszExecutionPayloadV3(ExecutionPayload payload)
    : SszExecutionPayloadV2(payload), ISszExecutionPayloadFactory<SszExecutionPayloadV3>
{
    protected override ExecutionPayloadV3 Inner => (ExecutionPayloadV3)base.Inner;

    public SszExecutionPayloadV3() : this(new ExecutionPayloadV3()) { }

    /// <inheritdoc/>
    public new static SszExecutionPayloadV3 From(Block block) => new(ExecutionPayloadV3.Create(block));

    public override ExecutionPayloadV3 AsExecutionPayload() => Inner;

    public ulong BlobGasUsed
    {
        get => Inner.BlobGasUsed ?? 0;
        set => Inner.BlobGasUsed = value;
    }

    public ulong ExcessBlobGas
    {
        get => Inner.ExcessBlobGas ?? 0;
        set => Inner.ExcessBlobGas = value;
    }
}

/// <summary>
/// Thin SSZ wrapper around <see cref="ExecutionPayloadV4"/>, extending
/// <see cref="SszExecutionPayloadV3"/> with EIP-7928 and EIP-7843 fields.
/// </summary>
[SszContainer]
public partial class SszExecutionPayloadV4(ExecutionPayloadV4 payload)
    : SszExecutionPayloadV3(payload), ISszExecutionPayloadFactory<SszExecutionPayloadV4>
{
    protected override ExecutionPayloadV4 Inner => (ExecutionPayloadV4)base.Inner;

    public SszExecutionPayloadV4() : this(new ExecutionPayloadV4()) { }

    /// <inheritdoc/>
    public new static SszExecutionPayloadV4 From(Block block) => new(ExecutionPayloadV4.Create(block));

    public override ExecutionPayloadV4 AsExecutionPayload() => Inner;

    [SszList(0x4000_0000)]
    public byte[] BlockAccessList
    {
        get => Inner.BlockAccessList ?? [];
        set => Inner.BlockAccessList = value.Length > 0 ? value : null;
    }

    public ulong SlotNumber
    {
        get => Inner.SlotNumber ?? 0;
        set => Inner.SlotNumber = value;
    }
}
