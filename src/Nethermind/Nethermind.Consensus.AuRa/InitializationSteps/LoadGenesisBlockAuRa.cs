// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
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
                if (_api.WorldState is null) throw new StepDependencyException(nameof(_api.StateProvider));

                _api.WorldState.CreateAccount(Address.Zero, UInt256.Zero);
                _api.WorldState.Commit(Homestead.Instance);
            }
        }
    }
}
