// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
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
        // Collects for both EIP-1559 and EIP-4844-Pectra
        public Address? FeeCollector => null;
        public UInt256? Eip1559BaseFeeMinValue => null;
        public UInt256 ForkBaseFee { get; }
        public UInt256 BaseFeeMaxChangeDenominator { get; }
        public long ElasticityMultiplier { get; }
    }

    public sealed class Eip1559Spec : IEip1559Spec
    {
        public bool IsEip1559Enabled { get; set; }
        public long Eip1559TransitionBlock { get; set; }
        public Address? FeeCollector { get; set; }
        public UInt256? Eip1559BaseFeeMinValue { get; set; }
        public UInt256 ForkBaseFee { get; set; }
        public UInt256 BaseFeeMaxChangeDenominator { get; set; }
        public long ElasticityMultiplier { get; set; }

        public Eip1559Spec(IEip1559Spec spec)
        {
            IsEip1559Enabled = spec.IsEip1559Enabled;
            Eip1559TransitionBlock = spec.Eip1559TransitionBlock;
            FeeCollector = spec.FeeCollector;
            Eip1559BaseFeeMinValue = spec.Eip1559BaseFeeMinValue;
            ForkBaseFee = spec.ForkBaseFee;
            BaseFeeMaxChangeDenominator = spec.BaseFeeMaxChangeDenominator;
            ElasticityMultiplier = spec.ElasticityMultiplier;
        }
    }
}
