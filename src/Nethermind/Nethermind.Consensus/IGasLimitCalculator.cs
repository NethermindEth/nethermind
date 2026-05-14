// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus
{
    public interface IGasLimitCalculator
    {
        long GetGasLimit(BlockHeader parentHeader);

        /// <summary>
        /// Computes the gas limit for the next block using <paramref name="targetGasLimitOverride"/>
        /// in place of any statically-configured target. Used to honor the per-FCU
        /// <c>targetGasLimit</c> introduced in PayloadAttributesV4 (Amsterdam).
        /// </summary>
        /// <remarks>
        /// Implementations that do not support a per-call target override should ignore it and fall
        /// back to <see cref="GetGasLimit(BlockHeader)"/>.
        /// </remarks>
        long GetGasLimit(BlockHeader parentHeader, long? targetGasLimitOverride) => GetGasLimit(parentHeader);
    }
}
