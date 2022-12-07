// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Forks
{
    public class Olympic : ReleaseSpec
    {
        private static IReleaseSpec _instance;

        protected Olympic()
        {
            Name = "Olympic";
            MaximumExtraDataSize = 32;
            MaxCodeSize = long.MaxValue;
            MinGasLimit = 5000;
            GasLimitBoundDivisor = 0x0400;
            BlockReward = UInt256.Parse("5000000000000000000");
            DifficultyBoundDivisor = 0x0800;
            IsEip3607Enabled = true;
            MaximumUncleCount = 2;
            Eip1559TransitionBlock = long.MaxValue;
            VerkleTreeTransitionTimeStamp = ulong.MaxValue;
            ValidateChainId = true;
            ValidateReceipts = true;
        }

        public static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, () => new Olympic());
        public override bool IsEip158IgnoredAccount(Address address) => false;
    }
}
