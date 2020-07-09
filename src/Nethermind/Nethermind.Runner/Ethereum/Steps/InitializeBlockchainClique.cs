//  Copyright (c) 2018 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using Nethermind.Blockchain;
using Nethermind.Blockchain.Processing;
using Nethermind.Blockchain.Rewards;
using Nethermind.Consensus;
using Nethermind.Consensus.Clique;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.Wallet;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitializeBlockchainClique : InitializeBlockchain
    {
        private readonly CliqueEthereumRunnerContext _context;

        public InitializeBlockchainClique(CliqueEthereumRunnerContext context) : base(context)
        {
            _context = context;
        }

        protected override void InitSealEngine()
        {
            if (_context.BlockTree == null) throw new StepDependencyException(nameof(_context.BlockTree));
            if (_context.ChainSpec == null) throw new StepDependencyException(nameof(_context.ChainSpec));
            if (_context.EthereumEcdsa == null) throw new StepDependencyException(nameof(_context.EthereumEcdsa));
            if (_context.DbProvider == null) throw new StepDependencyException(nameof(_context.DbProvider));
            if (_context.Signer == null) throw new StepDependencyException(nameof(_context.Signer));
        
            _context.RewardCalculatorSource = NoBlockRewards.Instance;
            CliqueConfig cliqueConfig = new CliqueConfig {BlockPeriod = _context.ChainSpec.Clique.Period, Epoch = _context.ChainSpec.Clique.Epoch};
            _context.SnapshotManager = new SnapshotManager(cliqueConfig, _context.DbProvider.BlocksDb, _context.BlockTree, _context.EthereumEcdsa, _context.LogManager);
            _context.SealValidator = new CliqueSealValidator(cliqueConfig, _context.SnapshotManager, _context.LogManager);
            _context.RecoveryStep = new CompositeDataRecoveryStep(_context.RecoveryStep, new AuthorRecoveryStep(_context.SnapshotManager));
            if (_context.Config<IInitConfig>().IsMining)
            {
                _context.Sealer = new CliqueSealer(_context.Signer, cliqueConfig, _context.SnapshotManager, _context.LogManager);
            }
            else
            {
                ((EthereumRunnerContext)_context).Sealer = NullSealEngine.Instance;
            }
        }
    }
}
