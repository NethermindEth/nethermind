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

using System;
using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using Nethermind.Abi;
using Nethermind.Blockchain.Rewards;
using Nethermind.Consensus.AuRa.Json;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class RewardContract : Contract, IActivatedAtBlock
    {
        public long Activation { get; }
        
        private static readonly AbiDefinition Definition = new AbiDefinitionParser().Parse<RewardContract>();
        
        public RewardContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress, long transitionBlock) : base(transactionProcessor, abiEncoder, contractAddress)
        {
            Activation = transitionBlock;
        }

        /// <summary>
        /// produce rewards for the given benefactors,
        /// with corresponding reward codes.
        /// only callable by `SYSTEM_ADDRESS`
        /// function reward(address[] benefactors, uint16[] kind) external returns (address[], uint256[]);
        /// </summary>
        /// <param name="blockHeader"></param>
        /// <param name="benefactors">benefactor addresses</param>
        /// <param name="kind">
        /// Kind:
        /// 0 - Author - Reward attributed to the block author
        /// 2 - Empty step - Reward attributed to the author(s) of empty step(s) included in the block (AuthorityRound engine)
        /// 3 - External - Reward attributed by an external protocol (e.g. block reward contract)
        /// 101-106 - Uncle - Reward attributed to uncles, with distance 1 to 6 (Ethash engine)
        /// </param>
        public (Address[] Addresses, UInt256[] Rewards) Reward(BlockHeader blockHeader, Address[] benefactors, ushort[] kind)
        {
            var result = Call(blockHeader, Definition.GetFunction(nameof(Reward)), Address.SystemUser, benefactors, kind);
            return ((Address[]) result[0], (UInt256[]) result[1]);
        }
    }
}