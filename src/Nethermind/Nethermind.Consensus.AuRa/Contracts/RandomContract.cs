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

using System.Numerics;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Serialization.Json.Abi;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class RandomContract : ConstantContract, IBlockTransitionable
    {
        private static readonly AbiDefinition Definition = new AbiDefinitionParser().Parse<RandomContract>();
        
        public RandomContract(
            ITransactionProcessor transactionProcessor,
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IStateProvider stateProvider, 
            IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource,
            long transitionBlock) : base(transactionProcessor, abiEncoder, contractAddress, stateProvider, readOnlyReadOnlyTransactionProcessorSource)
        {
            TransitionBlock = transitionBlock;
        }

        public long TransitionBlock { get; }
        
        public enum Phase {
            /// Waiting for the next phase.
            ///
            /// This state indicates either the successful revelation in this round or having missed the
            /// window to make a commitment, i.e. having failed to commit during the commit phase.
            Waiting,
            /// Indicates a commitment is possible, but still missing.
            BeforeCommit,
            /// Indicates a successful commitment, waiting for the commit phase to end.
            Committed,
            /// Indicates revealing is expected as the next step.
            Reveal
    }

        public Phase GetPhase(BlockHeader blockHeader)
        {
            UInt256 round = CurrentCollectRound(blockHeader);
        }

        private UInt256 CurrentCollectRound(BlockHeader blockHeader) => CallConstant<UInt256>(blockHeader, Definition.Functions["currentCollectRound"]);
        
        private UInt256 CurrentCollectRound(BlockHeader blockHeader) => CallConstant<UInt256>(blockHeader, Definition.Functions["currentCollectRound"]);
    }
}