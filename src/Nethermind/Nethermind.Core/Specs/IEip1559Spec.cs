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
        public ulong Eip1559TransitionBlock { get; }
        // Collects for both EIP-1559 and EIP-4844-Pectra
        public Address? FeeCollector => null;
        public UInt256? Eip1559BaseFeeMinValue => null;
        public UInt256 ForkBaseFee { get; }
        /// <summary>
        /// The maximum per-block base fee change is <c>1 / BaseFeeMaxChangeDenominator</c>; under EIP-8198, per
        /// <see cref="Eip8198Constants.BaseFeeReferenceSlotSeconds"/> of slot duration.
        /// </summary>
        public UInt256 BaseFeeMaxChangeDenominator { get; }

        /// <summary>
        /// EIP-8198: Quick Slots. Scales the maximum per-block base fee change with the slot duration read from the spacing
        /// of a block and its parent, see <see cref="DefaultBaseFeeCalculator"/>.
        /// </summary>
        /// <remarks>
        /// The slot length from the fork is the chain's <see cref="ISpecProvider.Eip8198SlotDurationMs"/>, used only to place
        /// slots; the blob parameters are chainspec <c>blobSchedule</c> data.
        /// </remarks>
        public bool IsEip8198Enabled { get; }
        public ulong ElasticityMultiplier { get; }
        public IBaseFeeCalculator BaseFeeCalculator { get; }
    }

    public sealed class OverridableEip1559Spec(IEip1559Spec spec) : IEip1559Spec
    {
        public bool IsEip1559Enabled { get; init; } = spec.IsEip1559Enabled;
        public ulong Eip1559TransitionBlock { get; init; } = spec.Eip1559TransitionBlock;
        public Address? FeeCollector { get; init; } = spec.FeeCollector;
        public UInt256? Eip1559BaseFeeMinValue { get; init; } = spec.Eip1559BaseFeeMinValue;
        public UInt256 ForkBaseFee { get; init; } = spec.ForkBaseFee;
        public UInt256 BaseFeeMaxChangeDenominator { get; init; } = spec.BaseFeeMaxChangeDenominator;
        public bool IsEip8198Enabled { get; init; } = spec.IsEip8198Enabled;
        public ulong ElasticityMultiplier { get; init; } = spec.ElasticityMultiplier;
        public IBaseFeeCalculator BaseFeeCalculator { get; init; } = spec.BaseFeeCalculator;
    }
}
