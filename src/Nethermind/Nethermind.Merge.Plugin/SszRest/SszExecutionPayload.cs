// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.IO;
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
public partial class SszExecutionPayloadV1(ExecutionPayload inner)
{
    public SszExecutionPayloadV1() : this(new ExecutionPayload())
    {
    }

    public ExecutionPayload Unwrap() => inner;

    public Hash256 ParentHash
    {
        get => inner.ParentHash;
        set => inner.ParentHash = value;
    }

    public Address FeeRecipient
    {
        get => inner.FeeRecipient;
        set => inner.FeeRecipient = value;
    }

    public Hash256 StateRoot
    {
        get => inner.StateRoot;
        set => inner.StateRoot = value;
    }

    public Hash256 ReceiptsRoot
    {
        get => inner.ReceiptsRoot;
        set => inner.ReceiptsRoot = value;
    }

    public Bloom LogsBloom
    {
        get => inner.LogsBloom;
        set => inner.LogsBloom = value;
    }

    public Hash256 PrevRandao
    {
        get => inner.PrevRandao;
        set => inner.PrevRandao = value;
    }

    public ulong BlockNumber
    {
        get => (ulong)inner.BlockNumber;
        set
        {
            if (value > (ulong)long.MaxValue)
                throw new InvalidDataException($"SSZ uint64 value {value} exceeds valid range for BlockNumber");
            inner.BlockNumber = (long)value;
        }
    }

    public ulong GasLimit
    {
        get => (ulong)inner.GasLimit;
        set
        {
            if (value > (ulong)long.MaxValue)
                throw new InvalidDataException($"SSZ uint64 value {value} exceeds valid range for GasLimit");
            inner.GasLimit = (long)value;
        }
    }

    public ulong GasUsed
    {
        get => (ulong)inner.GasUsed;
        set
        {
            if (value > (ulong)long.MaxValue)
                throw new InvalidDataException($"SSZ uint64 value {value} exceeds valid range for GasUsed");
            inner.GasUsed = (long)value;
        }
    }

    public ulong Timestamp
    {
        get => inner.Timestamp;
        set => inner.Timestamp = value;
    }

    [SszList(32)]
    public byte[] ExtraData
    {
        get => inner.ExtraData;
        set => inner.ExtraData = value;
    }

    public UInt256 BaseFeePerGas
    {
        get => inner.BaseFeePerGas;
        set => inner.BaseFeePerGas = value;
    }

    public Hash256 BlockHash
    {
        get => inner.BlockHash;
        set => inner.BlockHash = value;
    }

    [SszList(0x10_0000)]
    public SszTransaction[] Transactions
    {
        get
        {
            if (field is not null) return field;
            byte[][] txs = inner.Transactions;
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
                inner.Transactions = [];
                return;
            }
            byte[][] raw = new byte[value.Length][];
            for (int i = 0; i < value.Length; i++)
                raw[i] = value[i].Bytes ?? [];
            inner.Transactions = raw;
        }
    }
}

/// <summary>
/// Thin SSZ wrapper around <see cref="ExecutionPayload"/>.
/// Keeps all SSZ-specific type adaptations out of the domain class.
/// </summary>
[SszContainer]
public partial class SszExecutionPayload(ExecutionPayload inner)
{
    protected readonly ExecutionPayload _inner = inner;

    public SszExecutionPayload() : this(new ExecutionPayload())
    {
    }

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
        set
        {
            if (value > (ulong)long.MaxValue)
                throw new InvalidDataException($"SSZ uint64 value {value} exceeds valid range for BlockNumber");
            _inner.BlockNumber = (long)value;
        }
    }

    public ulong GasLimit
    {
        get => (ulong)_inner.GasLimit;
        set
        {
            if (value > (ulong)long.MaxValue)
                throw new InvalidDataException($"SSZ uint64 value {value} exceeds valid range for GasLimit");
            _inner.GasLimit = (long)value;
        }
    }

    public ulong GasUsed
    {
        get => (ulong)_inner.GasUsed;
        set
        {
            if (value > (ulong)long.MaxValue)
                throw new InvalidDataException($"SSZ uint64 value {value} exceeds valid range for GasUsed");
            _inner.GasUsed = (long)value;
        }
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
            if (field is not null) return field;
            byte[][] txs = _inner.Transactions;
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
    public SszWithdrawal[] Withdrawals
    {
        get
        {
            if (field is not null) return field;
            Withdrawal[]? ws = _inner.Withdrawals;
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
                _inner.Withdrawals = null;
                return;
            }
            if (value.Length == 0)
            {
                _inner.Withdrawals = [];
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
