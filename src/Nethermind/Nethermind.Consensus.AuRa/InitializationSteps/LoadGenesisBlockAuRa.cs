// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Autofac;
using Nethermind.Core;
using Nethermind.Init;
using Nethermind.Init.Steps;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.InitializationSteps
{
    public class LoadGenesisBlockAuRa : LoadGenesisBlock
    {
        private readonly AuRaNethermindApi _api;

        public LoadGenesisBlockAuRa(AuRaNethermindApi api) : base(api)
        {
            _api = api;
        }

        protected override void Load()
        {
            CreateSystemAccounts();
            base.Load();
        }

        private void CreateSystemAccounts()
        {
            if (_api.ChainSpec is null) throw new StepDependencyException(nameof(_api.ChainSpec));

            bool hasConstructorAllocation = _api.ChainSpec.Allocations.Values.Any(a => a.Constructor is not null);
            if (hasConstructorAllocation)
            {
                using ILifetimeScope statefulContainer = _api.Container.BeginLifetimeScope(NethermindScope.WorldState);
                IWorldState worldState = statefulContainer.Resolve<IWorldState>();
                worldState.CreateAccount(Address.Zero, UInt256.Zero);
                worldState.Commit(Homestead.Instance);
            }
        }
    }
}
