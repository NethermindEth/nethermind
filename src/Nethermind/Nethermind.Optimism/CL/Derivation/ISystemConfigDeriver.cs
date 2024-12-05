// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Merge.Plugin.Data;

namespace Nethermind.Optimism.CL;

public interface ISystemConfigDeriver
{
    SystemConfig SystemConfigFromL2Payload(ExecutionPayload l2Payload);
    SystemConfig UpdateSystemConfigFromL1BLock(SystemConfig systemConfig, BlockHeader l1Block);
}

/// <summary>
/// The rollup system configuration that carries over in every L2 block, and may be changed through L1 system config events.
/// The initial SystemConfig at rollup genesis is embedded in the rollup configuration.
/// </summary>
public struct SystemConfig
{
    /// <summary>
    /// Batch-sender address used in batch-inbox data-transaction filtering.
    /// </summary>
    public Address BatcherAddress;

    /// <summary>
    /// L2 block gas limit.
    /// </summary>
    public ulong GasLimit;

    /// <summary>
    /// L1 fee overhead.
	/// Pre-Ecotone this is passed as-is to the engine.
	/// Post-Ecotone this is always zero, and not passed into the engine.
    /// </summary>
    public byte[] /* 32 */ Overhead;

    /// <summary>
    /// L1 fee scalar
    /// Pre-Ecotone this is passed as-is to the engine.
    /// Post-Ecotone this encodes multiple pieces of scalar data.
    /// </summary>
    public byte[] /* 32 */ Scalar;

    /// <summary>
    /// Holocene-encoded EIP-1559 parameters.
    /// This value will be 0 if Holocene is not active, or if derivation has yet to
	/// process any EIP_1559_PARAMS system config update events.
    /// </summary>
    public byte[] /* 8 */ EIP1559Params;

    /// <summary>
    /// Indicates whether or not this struct should be
	/// marshaled in the pre-Holocene format. The pre-Holocene format does
	/// not marshal the EIP1559Params field. The presence of this field in
	/// pre-Holocene codebases causes the rollup config to be rejected.
    /// </summary>
    public bool MarshalPreHolocene;
}
