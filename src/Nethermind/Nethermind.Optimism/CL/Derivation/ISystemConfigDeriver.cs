// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers.Binary;
using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.JsonRpc.Data;

namespace Nethermind.Optimism.CL.Derivation;

public interface ISystemConfigDeriver
{
    SystemConfig SystemConfigFromL2BlockInfo(ReadOnlySpan<byte> data, ReadOnlySpan<byte> extraData, ulong gasLimit);
    SystemConfig UpdateSystemConfigFromL1BLockReceipts(SystemConfig systemConfig, ReceiptForRpc[] receipts);
}

/// <summary>
/// The rollup system configuration that carries over in every L2 block, and may be changed through L1 system config events.
/// The initial SystemConfig at rollup genesis is embedded in the rollup configuration.
/// </summary>
public record SystemConfig
{
    /// <summary>
    /// Batch-sender address used in batch-inbox data-transaction filtering.
    /// </summary>
    public Address BatcherAddress { get; init; } = Address.Zero;

    /// <summary>
    /// L2 block gas limit.
    /// </summary>
    public ulong GasLimit { get; init; }

    /// <summary>
    /// L1 fee overhead.
    /// Pre-Ecotone this is passed as-is to the engine.
    /// Post-Ecotone this is always zero, and not passed into the engine.
    /// </summary>
    public byte[] Overhead { get; init; } = new byte[32];

    /// <summary>
    /// L1 fee scalar
    /// Pre-Ecotone this is passed as-is to the engine.
    /// Post-Ecotone this encodes multiple pieces of scalar data.
    /// </summary>
    public byte[] Scalar { get; init; } = new byte[32];

    /// <summary>
    /// Holocene-encoded EIP-1559 parameters.
    /// This value will be 0 if Holocene is not active, or if derivation has yet to
    /// process any EIP_1559_PARAMS system config update events.
    /// </summary>
    public byte[] EIP1559Params { get; init; } = new byte[32];
    public uint BlobBaseFeeScalar => BinaryPrimitives.ReadUInt32BigEndian(Scalar.AsSpan(24, 4));
    public uint BaseFeeScalar => BinaryPrimitives.ReadUInt32BigEndian(Scalar.AsSpan(28, 4));

    public virtual bool Equals(SystemConfig? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return BatcherAddress.Equals(other.BatcherAddress)
               && GasLimit == other.GasLimit
               && Overhead.SequenceEqual(other.Overhead)
               && Scalar.SequenceEqual(other.Scalar)
               && EIP1559Params.SequenceEqual(other.EIP1559Params);
    }

    public override int GetHashCode() =>
        HashCode.Combine(BatcherAddress, GasLimit, Overhead, Scalar, EIP1559Params);

    public override string ToString()
    {
        return $"BatcherAddress: {BatcherAddress}, GasLimit: {GasLimit}, Overhead: {Overhead.ToHexString()}, Scalar: {Scalar.ToHexString()}, EIP1559Params: {EIP1559Params.ToHexString()}";
    }

    public static readonly SystemConfig Empty = new()
    {
        BatcherAddress = Address.Zero,
        EIP1559Params = [],
        GasLimit = 0,
        Overhead = [],
        Scalar = []
    };
}
