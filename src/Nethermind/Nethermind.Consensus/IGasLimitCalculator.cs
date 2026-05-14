// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Consensus
{
    public interface IGasLimitCalculator
    {
        /// <summary>
        /// Computes the gas limit for the next block. When <paramref name="targetGasLimitOverride"/> is supplied
        /// (e.g. from <c>PayloadAttributesV4.targetGasLimit</c> introduced in Amsterdam), it takes precedence over
        /// any statically-configured target. Implementations that don't honor a per-call target may ignore it.
        /// </summary>
        long GetGasLimit(BlockHeader parentHeader, long? targetGasLimitOverride = null);
    }
}
