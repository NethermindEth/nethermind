// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Int256;

namespace Nethermind.Core.Specs
{
    /// <summary>
    /// https://github.com/ethereum/EIPs
    /// </summary>
    public interface IEip1559Spec
    {
        /// <summary>
        /// Gas target and base fee, and fee burning.
        /// </summary>
        bool IsEip1559Enabled { get; }
        public long Eip1559TransitionBlock { get; }
        public Address? Eip1559FeeCollector => null;
        public UInt256? Eip1559BaseFeeMinValue => null;
    }
}
