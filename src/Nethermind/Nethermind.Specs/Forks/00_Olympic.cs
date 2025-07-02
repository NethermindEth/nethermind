// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;

namespace Nethermind.Specs.Forks
{
    public class Olympic : ReleaseSpec, INamedReleaseSpec
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
            ValidateChainId = true;
            ValidateReceipts = true;

            // The below addresses are added for all forks, but the given EIPs can be enabled at a specific timestamp or block.
            Eip7251ContractAddress = Eip7251Constants.ConsolidationRequestPredeployAddress;
            Eip7002ContractAddress = Eip7002Constants.WithdrawalRequestPredeployAddress;
            DepositContractAddress = Eip6110Constants.MainnetDepositContractAddress;
        }

        public bool Released { get; protected set; } = true;

        public static IReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new Olympic());
    }
}
