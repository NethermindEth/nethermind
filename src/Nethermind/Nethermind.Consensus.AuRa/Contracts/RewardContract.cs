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
using Nethermind.Core;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Serialization.Json.Abi;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class RewardContract : SystemContract, IBlockTransitionable
    {
        /// <summary>
        /// produce rewards for the given benefactors,
        /// with corresponding reward codes.
        /// only callable by `SYSTEM_ADDRESS`
        /// function reward(address[] benefactors, uint16[] kind) external returns (address[], uint256[]);
        ///
        /// Kind:
        /// 0 - Author - Reward attributed to the block author
        /// 2 - Empty step - Reward attributed to the author(s) of empty step(s) included in the block (AuthorityRound engine)
        /// 3 - External - Reward attributed by an external protocol (e.g. block reward contract)
        /// 101-106 - Uncle - Reward attributed to uncles, with distance 1 to 6 (Ethash engine)
        /// </summary>
        private const string RewardFunction = "reward";
        
        public long TransitionBlock { get; }
        
        private readonly IAbiEncoder _abiEncoder;
        
        private static readonly AbiDefinition Definition = new AbiDefinitionParser().Parse<RewardContract>();
        
        public RewardContract(ITransactionProcessor transactionProcessor, IAbiEncoder abiEncoder, Address contractAddress, long transitionBlock) : base(transactionProcessor, abiEncoder, contractAddress)
        {
            TransitionBlock = transitionBlock;
            _abiEncoder = abiEncoder ?? throw new ArgumentNullException(nameof(abiEncoder));
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
        public (Address[] Addresses, BigInteger[] Rewards) Reward(BlockHeader blockHeader, Address[] benefactors, ushort[] kind)
        {
            CallOutputTracer tracer = new CallOutputTracer();
            var rewardFunction = Definition.Functions[RewardFunction];
            var transaction = GenerateSystemTransaction(_abiEncoder.Encode(rewardFunction.GetCallInfo(), benefactors, kind));
            InvokeTransaction(blockHeader, transaction, tracer);
            var objects = _abiEncoder.Decode(rewardFunction.GetReturnInfo(), tracer.ReturnValue);
            return ((Address[]) objects[0], (BigInteger[]) objects[1]);
        }
    }
}