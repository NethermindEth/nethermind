// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core2.Types;

namespace Nethermind.Core2.Configuration
{
    public class ChainConstants
    {
        public ulong BaseRewardsPerEpoch { get; } = 4;

        public int DepositContractTreeDepth { get; } = 1 << 5;

        public Epoch FarFutureEpoch { get; } = new Epoch(ulong.MaxValue);

        public Epoch GenesisEpoch { get; } = Epoch.Zero;

        public Slot GenesisSlot { get; } = Slot.Zero;

        public int JustificationBitsLength { get; } = 4;

        public ulong MaximumDepositContracts { get; } = (ulong)1 << (1 << 5);
    }
}
