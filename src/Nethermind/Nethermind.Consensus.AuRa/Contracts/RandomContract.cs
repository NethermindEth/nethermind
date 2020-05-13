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

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class RandomContract : Contract, IActivatedAtBlock
    {
        private readonly Address _nodeAddress;
        private static readonly AbiDefinition Definition = new AbiDefinitionParser().Parse<RandomContract>();
        private ConstantContract Constant { get; }

        public RandomContract(ITransactionProcessor transactionProcessor,
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTransactionProcessorSource readOnlyReadOnlyTransactionProcessorSource,
            long transitionBlock,
            Address nodeAddress)
            : base(transactionProcessor, abiEncoder, contractAddress)
        {
            _nodeAddress = nodeAddress;
            ActivationBlock = transitionBlock;
            Constant = GetConstant(readOnlyReadOnlyTransactionProcessorSource);
        }

        public long ActivationBlock { get; }

        public enum Phase
        {
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

        public (Phase Phase, UInt256 Round) GetPhase(BlockHeader parentHeader)
        {
            this.ActivationCheck(parentHeader);
            
            UInt256 round = CurrentCollectRound(parentHeader);
            bool isCommitPhase = IsCommitPhase(parentHeader);
            bool isCommitted = IsCommitted(parentHeader, round);
            bool revealed = SentReveal(parentHeader, round);

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

        /// <summary>
        /// Returns a boolean flag of whether the specified validator has revealed their number for the specified collection round.
        /// </summary>
        /// <param name="parentHeader">Block header on which this is to be executed on.</param>
        /// <param name="collectRound">The serial number of the collection round for which the checkup should be done.</param>
        /// <returns>Boolean flag of whether the specified validator has revealed their number for the specified collection round.</returns>
        /// <remarks>
        /// The mining address of validator is last contract parameter.
        /// </remarks>
        private bool SentReveal(BlockHeader parentHeader, UInt256 collectRound) => Constant.Call<bool>(parentHeader, Definition.GetFunction(nameof(SentReveal)), _nodeAddress, collectRound, _nodeAddress);

        /// <summary>
        /// Returns a boolean flag indicating whether the specified validator has committed their secret's hash for the specified collection round.
        /// </summary>
        /// <param name="parentHeader">Block header on which this is to be executed on.</param>
        /// <param name="collectRound">The serial number of the collection round for which the checkup should be done.</param>
        /// <returns>Boolean flag indicating whether the specified validator has committed their secret's hash for the specified collection round.</returns>
        /// <remarks>
        /// The mining address of validator is last contract parameter.
        /// </remarks>
        private bool IsCommitted(BlockHeader parentHeader, UInt256 collectRound) => Constant.Call<bool>(parentHeader, Definition.GetFunction(nameof(IsCommitted)), _nodeAddress, collectRound, _nodeAddress);

        /// <summary>
        /// Returns the serial number of the current collection round.
        /// </summary>
        /// <param name="parentHeader">Block header on which this is to be executed on.</param>
        /// <returns>Serial number of the current collection round.</returns>
        private UInt256 CurrentCollectRound(BlockHeader parentHeader) => Constant.Call<UInt256>(parentHeader, Definition.GetFunction(nameof(CurrentCollectRound)), _nodeAddress);

        /// <summary>
        /// Returns a boolean flag indicating whether the current phase of the current collection round is a `commits phase`.
        /// Used by the validator's node to determine if it should commit the hash of the secret during the current collection round.
        /// </summary>
        /// <param name="parentHeader">Block header on which this is to be executed on.</param>
        /// <returns>Boolean flag indicating whether the current phase of the current collection round is a `commits phase`.</returns>
        private bool IsCommitPhase(BlockHeader parentHeader) => Constant.Call<bool>(parentHeader, Definition.GetFunction(nameof(IsCommitPhase)), _nodeAddress);

        /// <summary>
        /// Returns the Keccak-256 hash and cipher of the validator's secret for the specified collection round and the specified validator stored by the validator through the `commitHash` function.
        /// </summary>
        /// <param name="parentHeader">Block header on which this is to be executed on.</param>
        /// <param name="collectRound">The serial number of the collection round for which hash and cipher should be retrieved.</param>
        /// <returns>Keccak-256 hash and cipher of the validator's secret for the specified collection round and the specified validator stored by the validator through the `commitHash` function.</returns>
        /// <remarks>
        /// The mining address of validator is last contract parameter.
        /// </remarks>
        public (Keccak Hash, byte[] Cipher) GetCommitAndCipher(BlockHeader parentHeader, UInt256 collectRound)
        {
            var (hash, cipher) = Constant.Call<byte[], byte[]>(parentHeader, Definition.GetFunction(nameof(GetCommitAndCipher)), _nodeAddress, collectRound, _nodeAddress);
            return (new Keccak(hash), cipher);
        }

        /// <summary>
        /// Called by the validator's node to store a hash and a cipher of the validator's secret on each collection round.
        /// The validator's node must use its mining address to call this function.
        /// This function can only be called once per collection round (during the `commits phase`).
        /// </summary>
        /// <param name="secretHash">The Keccak-256 hash of the validator's secret.</param>
        /// <param name="cipher">The cipher of the validator's secret. Can be used by the node to restore the lost secret after the node is restarted (see the `getCipher` getter).</param>
        /// <returns>Transaction to be included in block.</returns>
        public Transaction CommitHash(in Keccak secretHash, byte[] cipher) => GenerateTransaction<GeneratedTransaction>(Definition.GetFunction(nameof(CommitHash)), _nodeAddress, secretHash.Bytes, cipher);

        /// <summary>
        /// Called by the validator's node to XOR its number with the current random seed.
        /// The validator's node must use its mining address to call this function.
        /// This function can only be called once per collection round (during the `reveals phase`).
        /// </summary>
        /// <param name="number">The validator's number.</param>
        /// <returns>Transaction to be included in block.</returns>
        public Transaction RevealNumber(UInt256 number) => GenerateTransaction<GeneratedTransaction>(Definition.GetFunction(nameof(RevealNumber)), _nodeAddress, number);
    }
}