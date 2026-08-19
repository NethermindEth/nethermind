// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Stateless.Execution.IO;

/// <summary>SSZ execution payload of a block on the chain's currently deployed fork.</summary>
/// <remarks>
/// An <see href="https://eips.ethereum.org/EIPS/eip-7688">EIP-7688</see> progressive container
/// holding the stable field indices 0-16; <see cref="SszExecutionPayloadAmsterdam"/> activates
/// the two fields Amsterdam appends. Every field property below is a pass-through to the
/// like-named <see cref="ExecutionPayloadV3"/> member, so the wire layout is defined by the
/// <c>SszField</c> indices rather than by declaration order.
/// </remarks>
[SszContainer]
public partial class SszExecutionPayload(ExecutionPayloadV3 payload)
{
    /// <summary>Creates an empty payload for the SSZ decoder to populate.</summary>
    public SszExecutionPayload() : this(new ExecutionPayloadV3()) { }

    /// <summary>Creates the SSZ execution payload of a block.</summary>
    /// <param name="block">The block to wrap.</param>
    public static SszExecutionPayload From(Block block) => new(ExecutionPayloadV3.Create(block));

    /// <summary>Gets the domain payload the field properties read from and write to.</summary>
    /// <remarks>Derived containers narrow this to their own payload type to reach the fields their fork adds.</remarks>
    protected virtual ExecutionPayloadV3 Inner { get; private set; } = payload;

    /// <summary>Gets the wrapped domain execution payload.</summary>
    public virtual ExecutionPayloadV3 AsExecutionPayload() => Inner;

    [SszField(0)]
    public Hash256 ParentHash
    {
        get => Inner.ParentHash;
        set => Inner.ParentHash = value;
    }

    [SszField(1)]
    public Address FeeRecipient
    {
        get => Inner.FeeRecipient;
        set => Inner.FeeRecipient = value;
    }

    [SszField(2)]
    public Hash256 StateRoot
    {
        get => Inner.StateRoot;
        set => Inner.StateRoot = value;
    }

    [SszField(3)]
    public Hash256 ReceiptsRoot
    {
        get => Inner.ReceiptsRoot;
        set => Inner.ReceiptsRoot = value;
    }

    [SszField(4)]
    public Bloom LogsBloom
    {
        get => Inner.LogsBloom;
        set => Inner.LogsBloom = value;
    }

    [SszField(5)]
    public Hash256 PrevRandao
    {
        get => Inner.PrevRandao;
        set => Inner.PrevRandao = value;
    }

    [SszField(6)]
    public ulong BlockNumber
    {
        get => Inner.BlockNumber;
        set => Inner.BlockNumber = value;
    }

    [SszField(7)]
    public ulong GasLimit
    {
        get => Inner.GasLimit;
        set => Inner.GasLimit = value;
    }

    [SszField(8)]
    public ulong GasUsed
    {
        get => Inner.GasUsed;
        set => Inner.GasUsed = value;
    }

    [SszField(9)]
    public ulong Timestamp
    {
        get => Inner.Timestamp;
        set => Inner.Timestamp = value;
    }

    [SszField(10)]
    [SszList(32)]
    public byte[] ExtraData
    {
        get => Inner.ExtraData;
        set => Inner.ExtraData = value;
    }

    [SszField(11)]
    public UInt256 BaseFeePerGas
    {
        get => Inner.BaseFeePerGas;
        set => Inner.BaseFeePerGas = value;
    }

    [SszField(12)]
    public Hash256 BlockHash
    {
        get => Inner.BlockHash;
        set => Inner.BlockHash = value;
    }

    [SszField(13)]
    [SszProgressiveList]
    public SszProgressiveBytes[] Transactions
    {
        get
        {
            if (field is not null) return field;
            byte[][] txs = Inner.Transactions;
            if (txs.Length == 0) return [];
            field = new SszProgressiveBytes[txs.Length];
            for (int i = 0; i < txs.Length; i++)
                field[i] = new SszProgressiveBytes { Bytes = txs[i] };
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

    [SszField(14)]
    [SszProgressiveList]
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

    [SszField(15)]
    public ulong BlobGasUsed
    {
        get => Inner.BlobGasUsed ?? 0;
        set => Inner.BlobGasUsed = value;
    }

    [SszField(16)]
    public ulong ExcessBlobGas
    {
        get => Inner.ExcessBlobGas ?? 0;
        set => Inner.ExcessBlobGas = value;
    }
}

/// <summary>SSZ execution payload of an Amsterdam block, adding the EIP-7928 and EIP-7843 fields.</summary>
[SszContainer]
public partial class SszExecutionPayloadAmsterdam(ExecutionPayloadV4 payload) : SszExecutionPayload(payload)
{
    /// <inheritdoc cref="SszExecutionPayload()"/>
    public SszExecutionPayloadAmsterdam() : this(new ExecutionPayloadV4()) { }

    /// <inheritdoc cref="SszExecutionPayload.From(Block)"/>
    public new static SszExecutionPayloadAmsterdam From(Block block) => new(ExecutionPayloadV4.Create(block));

    protected override ExecutionPayloadV4 Inner => (ExecutionPayloadV4)base.Inner;

    /// <inheritdoc/>
    public override ExecutionPayloadV4 AsExecutionPayload() => Inner;

    [SszField(17)]
    [SszProgressiveList]
    public byte[] BlockAccessList
    {
        get => Inner.BlockAccessList ?? [];
        set => Inner.BlockAccessList = value.Length > 0 ? value : null;
    }

    [SszField(18)]
    public ulong SlotNumber
    {
        get => Inner.SlotNumber ?? 0;
        set => Inner.SlotNumber = value;
    }
}

/// <summary>SSZ <c>ProgressiveByteList</c> as defined by <see href="https://eips.ethereum.org/EIPS/eip-7688">EIP-7688</see>.</summary>
[SszContainer(isCollectionItself: true)]
public partial struct SszProgressiveBytes
{
    /// <summary>Gets or sets the encoded bytes of one list element, or <c>null</c> for an empty one.</summary>
    [SszProgressiveList]
    public byte[]? Bytes { get; set; }
}

/// <summary>SSZ mirror of a consensus-layer withdrawal, carried by <see cref="SszExecutionPayload.Withdrawals"/>.</summary>
[SszContainer]
public partial struct SszWithdrawal
{
    /// <inheritdoc cref="Withdrawal.Index"/>
    public ulong Index { get; set; }

    /// <inheritdoc cref="Withdrawal.ValidatorIndex"/>
    public ulong ValidatorIndex { get; set; }

    /// <inheritdoc cref="Withdrawal.Address"/>
    public Address Address { get; set; }

    /// <inheritdoc cref="Withdrawal.AmountInGwei"/>
    public ulong Amount { get; set; }
}
