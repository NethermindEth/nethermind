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
/// that <see cref="SszExecutionPayload"/> would include in the fixed header.
/// </summary>
[SszContainer]
public partial class SszExecutionPayloadV1
{
    private readonly ExecutionPayload _inner;

    private SszTransaction[]? _transactions;

    public SszExecutionPayloadV1() => _inner = new ExecutionPayload();
    public SszExecutionPayloadV1(ExecutionPayload inner) => _inner = inner;

    public ExecutionPayload Unwrap() => _inner;

    public Hash256 ParentHash
    {
        get => _inner.ParentHash;
        set => _inner.ParentHash = value;
    }

    public Address FeeRecipient
    {
        get => _inner.FeeRecipient;
        set => _inner.FeeRecipient = value;
    }

    public Hash256 StateRoot
    {
        get => _inner.StateRoot;
        set => _inner.StateRoot = value;
    }

    public Hash256 ReceiptsRoot
    {
        get => _inner.ReceiptsRoot;
        set => _inner.ReceiptsRoot = value;
    }

    public Bloom LogsBloom
    {
        get => _inner.LogsBloom;
        set => _inner.LogsBloom = value;
    }

    public Hash256 PrevRandao
    {
        get => _inner.PrevRandao;
        set => _inner.PrevRandao = value;
    }

    public ulong BlockNumber
    {
        get => (ulong)_inner.BlockNumber;
        set => _inner.BlockNumber = (long)value;
    }

    public ulong GasLimit
    {
        get => (ulong)_inner.GasLimit;
        set => _inner.GasLimit = (long)value;
    }

    public ulong GasUsed
    {
        get => (ulong)_inner.GasUsed;
        set => _inner.GasUsed = (long)value;
    }

    public ulong Timestamp
    {
        get => _inner.Timestamp;
        set => _inner.Timestamp = value;
    }

    [SszList(32)]
    public byte[] ExtraData
    {
        get => _inner.ExtraData;
        set => _inner.ExtraData = value;
    }

    public UInt256 BaseFeePerGas
    {
        get => _inner.BaseFeePerGas;
        set => _inner.BaseFeePerGas = value;
    }

    public Hash256 BlockHash
    {
        get => _inner.BlockHash;
        set => _inner.BlockHash = value;
    }

    [SszList(0x10_0000)]
    public SszTransaction[] Transactions
    {
        get
        {
            if (_transactions is not null) return _transactions;
            byte[][] txs = _inner.Transactions;
            if (txs.Length == 0) return [];
            _transactions = new SszTransaction[txs.Length];
            for (int i = 0; i < txs.Length; i++)
                _transactions[i] = new SszTransaction { Bytes = txs[i] };
            return _transactions;
        }
        set
        {
            _transactions = value;
            if (value is null || value.Length == 0)
            {
                _inner.Transactions = [];
                return;
            }
            byte[][] raw = new byte[value.Length][];
            for (int i = 0; i < value.Length; i++)
                raw[i] = value[i].Bytes ?? [];
            _inner.Transactions = raw;
        }
    }
}

/// <summary>
/// Thin SSZ wrapper around <see cref="ExecutionPayload"/>.
/// Keeps all SSZ-specific type adaptations out of the domain class.
/// </summary>
[SszContainer]
public partial class SszExecutionPayload
{
    protected readonly ExecutionPayload _inner;

    private SszTransaction[]? _transactions;
    private WithdrawalWire[]? _withdrawals;

    public SszExecutionPayload() => _inner = new ExecutionPayload();
    public SszExecutionPayload(ExecutionPayload inner) => _inner = inner;

    public ExecutionPayload Unwrap() => _inner;

    public Hash256 ParentHash
    {
        get => _inner.ParentHash;
        set => _inner.ParentHash = value;
    }

    public Address FeeRecipient
    {
        get => _inner.FeeRecipient;
        set => _inner.FeeRecipient = value;
    }

    public Hash256 StateRoot
    {
        get => _inner.StateRoot;
        set => _inner.StateRoot = value;
    }

    public Hash256 ReceiptsRoot
    {
        get => _inner.ReceiptsRoot;
        set => _inner.ReceiptsRoot = value;
    }

    public Bloom LogsBloom
    {
        get => _inner.LogsBloom;
        set => _inner.LogsBloom = value;
    }

    public Hash256 PrevRandao
    {
        get => _inner.PrevRandao;
        set => _inner.PrevRandao = value;
    }

    public ulong BlockNumber
    {
        get => (ulong)_inner.BlockNumber;
        set => _inner.BlockNumber = (long)value;
    }

    public ulong GasLimit
    {
        get => (ulong)_inner.GasLimit;
        set => _inner.GasLimit = (long)value;
    }

    public ulong GasUsed
    {
        get => (ulong)_inner.GasUsed;
        set => _inner.GasUsed = (long)value;
    }

    public ulong Timestamp
    {
        get => _inner.Timestamp;
        set => _inner.Timestamp = value;
    }

    [SszList(32)]
    public byte[] ExtraData
    {
        get => _inner.ExtraData;
        set => _inner.ExtraData = value;
    }

    public UInt256 BaseFeePerGas
    {
        get => _inner.BaseFeePerGas;
        set => _inner.BaseFeePerGas = value;
    }

    public Hash256 BlockHash
    {
        get => _inner.BlockHash;
        set => _inner.BlockHash = value;
    }

    [SszList(0x10_0000)]
    public SszTransaction[] Transactions
    {
        get
        {
            if (_transactions is not null) return _transactions;
            byte[][] txs = _inner.Transactions;
            if (txs.Length == 0) return [];
            _transactions = new SszTransaction[txs.Length];
            for (int i = 0; i < txs.Length; i++)
                _transactions[i] = new SszTransaction { Bytes = txs[i] };
            return _transactions;
        }
        set
        {
            _transactions = value;
            if (value is null || value.Length == 0)
            {
                _inner.Transactions = [];
                return;
            }
            byte[][] raw = new byte[value.Length][];
            for (int i = 0; i < value.Length; i++)
                raw[i] = value[i].Bytes ?? [];
            _inner.Transactions = raw;
        }
    }

    [SszList(16)]
    public WithdrawalWire[] Withdrawals
    {
        get
        {
            if (_withdrawals is not null) return _withdrawals;
            Withdrawal[]? ws = _inner.Withdrawals;
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
                _inner.Withdrawals = null;
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
            _inner.Withdrawals = result;
        }
    }
}

/// <summary>
/// Thin SSZ wrapper around <see cref="ExecutionPayloadV3"/>, extending
/// <see cref="SszExecutionPayload"/> with EIP-4844 fields.
/// </summary>
[SszContainer]
public partial class SszExecutionPayloadV3 : SszExecutionPayload
{
    private ExecutionPayloadV3 InnerV3 => (ExecutionPayloadV3)_inner;

    public SszExecutionPayloadV3() : base(new ExecutionPayloadV3()) { }
    public SszExecutionPayloadV3(ExecutionPayloadV3 inner) : base(inner) { }

    public new ExecutionPayloadV3 Unwrap() => InnerV3;

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
}

/// <summary>
/// Thin SSZ wrapper around <see cref="ExecutionPayloadV4"/>, extending
/// <see cref="SszExecutionPayloadV3"/> with EIP-7928 and EIP-7843 fields.
/// </summary>
[SszContainer]
public partial class SszExecutionPayloadV4 : SszExecutionPayloadV3
{
    private ExecutionPayloadV4 InnerV4 => (ExecutionPayloadV4)_inner;

    public SszExecutionPayloadV4() : base(new ExecutionPayloadV4()) { }
    public SszExecutionPayloadV4(ExecutionPayloadV4 inner) : base(inner) { }

    public new ExecutionPayloadV4 Unwrap() => InnerV4;

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
}
