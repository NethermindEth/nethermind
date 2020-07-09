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

using Nethermind.Blockchain.Rewards;
using Nethermind.Consensus;
using Nethermind.Consensus.Ethash;
using Nethermind.Runner.Ethereum.Context;

namespace Nethermind.Runner.Ethereum.Steps
{
    public class InitializeBlockchainEthash : InitializeBlockchain
    {
        private readonly EthashEthereumRunnerContext _context;

        public InitializeBlockchainEthash(EthashEthereumRunnerContext context) : base(context)
        {
            _context = context;
        }

        protected override void InitSealEngine()
        {
            _context.RewardCalculatorSource = new RewardCalculator(_context.SpecProvider);
            DifficultyCalculator difficultyCalculator = new DifficultyCalculator(_context.SpecProvider);
            _context.Sealer = _context.Config<IInitConfig>().IsMining ? (ISealer) new EthashSealer(new Ethash(_context.LogManager), _context.Signer, _context.LogManager) : NullSealEngine.Instance;
            _context.SealValidator = new EthashSealValidator(_context.LogManager, difficultyCalculator, _context.CryptoRandom, new Ethash(_context.LogManager));
        }
    }
}
