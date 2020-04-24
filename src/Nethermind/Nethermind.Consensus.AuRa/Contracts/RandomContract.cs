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
using System.Numerics;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.Serialization.Json.Abi;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class RandomContract : Contract, IBlockTransitionable
    {
        private readonly Address _nodeAddress;
        private static readonly AbiDefinition Definition = new AbiDefinitionParser().Parse<RandomContract>();
        private ConstantContract Constant { get; }

        public RandomContract(ITransactionProcessor transactionProcessor,
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IStateProvider stateProvider,
            IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource,
            long transitionBlock, 
            Address nodeAddress) 
            : base(transactionProcessor, abiEncoder, contractAddress)
        {
            _nodeAddress = nodeAddress;
            TransitionBlock = transitionBlock;
            Constant = GetConstant(stateProvider, readOnlyReadOnlyTransactionProcessorSource);
        }

        public long TransitionBlock { get; }
        
        public enum Phase {
            /// <summary>
            /// Waiting for the next phase.
            ///
            /// This state indicates either the successful revelation in this round or having missed the
            /// window to make a commitment, i.e. having failed to commit during the commit phase.
            /// </summary>
            Waiting,
            
            /// <summary>
            /// Indicates a commitment is possible, but still missing.
            /// </summary>
            BeforeCommit,
            
            /// <summary>
            /// Indicates a successful commitment, waiting for the commit phase to end.
            /// </summary>
            Committed,
            
            /// <summary>
            /// Indicates revealing is expected as the next step.
            /// </summary>
            Reveal
    }

        public (Phase Phase, UInt256 Round) GetPhase(BlockHeader blockHeader)
        {
            UInt256 round = CurrentCollectRound(blockHeader);
            bool isCommitPhase = IsCommitPhase(blockHeader);
            bool isCommitted = IsCommitted(blockHeader, round);
            bool revealed = SentReveal(blockHeader, round);

            var phase = isCommitPhase
                ? revealed
                    ? throw new InvalidOperationException("Revealed random number during commit phase.")
                    : !isCommitted
                        ? Phase.BeforeCommit
                        : Phase.Committed
                : !isCommitted // We apparently entered too late to make a commitment, wait until we get a chance again. 
                  || revealed
                    ? Phase.Waiting
                    : Phase.Reveal;
            
            return (phase, round);
        }

        private bool SentReveal(BlockHeader blockHeader, UInt256 round)  => Constant.Call<bool>(blockHeader, Definition.GetFunction(nameof(SentReveal)), _nodeAddress, round, _nodeAddress);

        private bool IsCommitted(BlockHeader blockHeader, UInt256 round) => Constant.Call<bool>(blockHeader, Definition.GetFunction(nameof(IsCommitted)), _nodeAddress, round, _nodeAddress);
        
        private UInt256 CurrentCollectRound(BlockHeader blockHeader) => Constant.Call<UInt256>(blockHeader, Definition.GetFunction(nameof(CurrentCollectRound)), _nodeAddress);
        
        private bool IsCommitPhase(BlockHeader blockHeader) => Constant.Call<bool>(blockHeader, Definition.GetFunction(nameof(IsCommitPhase)), _nodeAddress);
        
        public (Keccak hash, byte[] cipher) GetCommitAndCipher(BlockHeader blockHeader, UInt256 round)
        {
            var (hash, cipher) = Constant.Call<byte[], byte[]>(blockHeader, Definition.GetFunction(nameof(GetCommitAndCipher)), _nodeAddress, round, _nodeAddress);
            return (new Keccak(hash), cipher);
        }

        public Transaction CommitHash(in Keccak hash, byte[] cipher) => GenerateTransaction(Definition.GetFunction(nameof(CommitHash)), _nodeAddress, hash.Bytes, cipher);

        public Transaction RevealNumber(UInt256 number) => GenerateTransaction(Definition.GetFunction(nameof(RevealNumber)), _nodeAddress, number);
    }

    internal static class ContractTransactionExtensions
    {
        public static Transaction WithNonce(this Transaction tx, IStateProvider stateProvider)
        {
            return tx;
        }
    }
}