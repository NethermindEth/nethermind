// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using System.Threading.Tasks;
using Nethermind.Api;
using Nethermind.Core;
using Nethermind.Evm.State;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public class LoadGenesisBlockAuRa : LoadGenesisBlock
    {
        private readonly AuRaNethermindApi _api;

        public LoadGenesisBlockAuRa(AuRaNethermindApi api) : base(api)
        {
            _api = api;
        }

        protected override async Task Load(IMainProcessingContext mainProcessingContext)
        {
            CreateSystemAccounts(mainProcessingContext.WorldState);
            await base.Load(mainProcessingContext);
        }

        private void CreateSystemAccounts(IWorldState worldState)
        {
            if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));

            bool hasConstructorAllocation = _api.ChainSpec.Allocations.Values.Any(static a => a.Constructor is not null);
            if (hasConstructorAllocation)
            {
                worldState.CreateAccount(Address.Zero, UInt256.Zero);
                worldState.Commit(Homestead.Instance);
            }
        }
    }
}
