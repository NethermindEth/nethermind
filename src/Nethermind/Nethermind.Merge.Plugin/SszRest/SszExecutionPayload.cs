// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Merge.Plugin.Data;
using Nethermind.Serialization.Ssz;

namespace Nethermind.Merge.Plugin.SszRest;

/// <summary>
/// SSZ wrapper for ExecutionPayloadV1 (pre-Shanghai / pre-EIP-4895).
/// Using this type for V1 newPayload encode/decode avoids the 4-byte withdrawals offset
/// that <see cref="ExecutionPayloadSsz"/> would include in the fixed header, and also
/// eliminates the need to temporarily mutate the domain object when encoding V1 responses.
/// </summary>
[SszContainer]
public partial class ExecutionPayloadV1Ssz
{
    [SszIgnore] public ExecutionPayload Inner { get; set; } = new();

    private SszTransaction[]? _transactions;

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
        get => (ulong)Inner.BlockNumber;
        set => Inner.BlockNumber = (long)value;
    }

    public ulong GasLimit
    {
        get => (ulong)Inner.GasLimit;
        set => Inner.GasLimit = (long)value;
    }

    public ulong GasUsed
    {
        get => (ulong)Inner.GasUsed;
        set => Inner.GasUsed = (long)value;
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
            if (_transactions is not null) return _transactions;
            byte[][] txs = Inner.Transactions;
            if (txs.Length == 0) return [];
            _transactions = new SszTransaction[txs.Length];
            for (int i = 0; i < txs.Length; i++)
                _transactions[i] = new SszTransaction { Data = txs[i] };
            return _transactions;
        }
        set
        {
            _transactions = value;
            if (value is null || value.Length == 0)
            {
                Inner.Transactions = [];
                return;
            }
            byte[][] raw = new byte[value.Length][];
            for (int i = 0; i < value.Length; i++)
                raw[i] = value[i].Data ?? [];
            Inner.Transactions = raw;
        }
    }

    public static ExecutionPayloadV1Ssz Wrap(ExecutionPayload payload) => new() { Inner = payload };

    public static ExecutionPayload Unwrap(ExecutionPayloadV1Ssz wrapper) => wrapper.Inner;
}

/// <summary>
/// Thin SSZ wrapper around <see cref="ExecutionPayload"/>.
/// Keeps all SSZ-specific type adaptations out of the domain class.
/// </summary>
[SszContainer]
public partial class ExecutionPayloadSsz
{
    [SszIgnore] public ExecutionPayload Inner { get; set; } = new();

    private SszTransaction[]? _transactions;
    private WithdrawalWire[]? _withdrawals;

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
        get => (ulong)Inner.BlockNumber;
        set => Inner.BlockNumber = (long)value;
    }

    public ulong GasLimit
    {
        get => (ulong)Inner.GasLimit;
        set => Inner.GasLimit = (long)value;
    }

    public ulong GasUsed
    {
        get => (ulong)Inner.GasUsed;
        set => Inner.GasUsed = (long)value;
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
            if (_transactions is not null) return _transactions;
            byte[][] txs = Inner.Transactions;
            if (txs.Length == 0) return [];
            _transactions = new SszTransaction[txs.Length];
            for (int i = 0; i < txs.Length; i++)
                _transactions[i] = new SszTransaction { Data = txs[i] };
            return _transactions;
        }
        set
        {
            _transactions = value;
            if (value is null || value.Length == 0)
            {
                Inner.Transactions = [];
                return;
            }
            byte[][] raw = new byte[value.Length][];
            for (int i = 0; i < value.Length; i++)
                raw[i] = value[i].Data ?? [];
            Inner.Transactions = raw;
        }
    }

    [SszList(16)]
    public WithdrawalWire[] Withdrawals
    {
        get
        {
            if (_withdrawals is not null) return _withdrawals;
            Withdrawal[]? ws = Inner.Withdrawals;
            if (ws is null || ws.Length == 0) return [];
            _withdrawals = new WithdrawalWire[ws.Length];
            for (int i = 0; i < ws.Length; i++)
                _withdrawals[i] = new WithdrawalWire
                {
                    Index = ws[i].Index,
                    ValidatorIndex = ws[i].ValidatorIndex,
                    Address = ws[i].Address,
                    Amount = ws[i].AmountInGwei
                };
            return _withdrawals;
        }
        set
        {
            _withdrawals = value;
            if (value is null || value.Length == 0)
            {
                Inner.Withdrawals = null;
                return;
            }
            Withdrawal[] result = new Withdrawal[value.Length];
            for (int i = 0; i < value.Length; i++)
                result[i] = new Withdrawal
                {
                    Index = value[i].Index,
                    ValidatorIndex = value[i].ValidatorIndex,
                    Address = value[i].Address,
                    AmountInGwei = value[i].Amount
                };
            Inner.Withdrawals = result;
        }
    }

    public static ExecutionPayloadSsz Wrap(ExecutionPayload payload) => new() { Inner = payload };

    public static ExecutionPayload Unwrap(ExecutionPayloadSsz wrapper) => wrapper.Inner;
}

/// <summary>
/// Thin SSZ wrapper around <see cref="ExecutionPayloadV3"/>, extending
/// <see cref="ExecutionPayloadSsz"/> with EIP-4844 fields.
/// </summary>
[SszContainer]
public partial class ExecutionPayloadV3Ssz : ExecutionPayloadSsz
{
    public ExecutionPayloadV3Ssz() => Inner = new ExecutionPayloadV3();

    [SszIgnore]
    private ExecutionPayloadV3 InnerV3 => (ExecutionPayloadV3)Inner;

    public ulong BlobGasUsed
    {
        get => InnerV3.BlobGasUsed ?? 0;
        set => InnerV3.BlobGasUsed = value;
    }

    public ulong ExcessBlobGas
    {
        get => InnerV3.ExcessBlobGas ?? 0;
        set => InnerV3.ExcessBlobGas = value;
    }

    public static ExecutionPayloadV3Ssz Wrap(ExecutionPayloadV3 payload) =>
        new() { Inner = payload };

    public static ExecutionPayloadV3 Unwrap(ExecutionPayloadV3Ssz wrapper) =>
        (ExecutionPayloadV3)wrapper.Inner;
}

/// <summary>
/// Thin SSZ wrapper around <see cref="ExecutionPayloadV4"/>, extending
/// <see cref="ExecutionPayloadV3Ssz"/> with EIP-7928 and EIP-7843 fields.
/// </summary>
[SszContainer]
public partial class ExecutionPayloadV4Ssz : ExecutionPayloadV3Ssz
{
    public ExecutionPayloadV4Ssz() => Inner = new ExecutionPayloadV4();

    [SszIgnore]
    private ExecutionPayloadV4 InnerV4 => (ExecutionPayloadV4)Inner;

    [SszList(0x4000_0000)]
    public byte[] BlockAccessList
    {
        get => InnerV4.BlockAccessList ?? [];
        set => InnerV4.BlockAccessList = value.Length > 0 ? value : null;
    }

    public ulong SlotNumber
    {
        get => InnerV4.SlotNumber ?? 0;
        set => InnerV4.SlotNumber = value;
    }

    public static ExecutionPayloadV4Ssz Wrap(ExecutionPayloadV4 payload) =>
        new() { Inner = payload };

    public static ExecutionPayloadV4 Unwrap(ExecutionPayloadV4Ssz wrapper) =>
        (ExecutionPayloadV4)wrapper.Inner;
}
