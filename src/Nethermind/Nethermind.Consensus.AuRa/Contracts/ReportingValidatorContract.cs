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

using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Evm;
using Nethermind.Serialization.Json.Abi;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public class ReportingValidatorContract : Contract
    {
        private readonly Address _nodeAddress;
        private static readonly AbiDefinition Definition = new AbiDefinitionParser().Parse<ReportingValidatorContract>();
        
        public ReportingValidatorContract(
            ITransactionProcessor transactionProcessor, 
            IAbiEncoder abiEncoder, 
            Address contractAddress,
            Address nodeAddress)
            : base(transactionProcessor, abiEncoder, contractAddress)
        {
            _nodeAddress = nodeAddress;
        }

        /// <summary>
        /// Reports that the malicious validator misbehaved at the specified block.
        /// Called by the node of each honest validator after the specified validator misbehaved.
        /// <seealso>
        ///     <cref>https://openethereum.github.io/wiki/Validator-Set.html#reporting-contract</cref>
        /// </seealso>
        /// </summary>
        /// <param name="maliciousMiningAddress">The mining address of the malicious validator.</param>
        /// <param name="blockNumber">The block number where the misbehavior was observed.</param>
        /// <param name="proof">Proof of misbehavior.</param>
        /// <returns>Transaction to be added to pool.</returns>
        public Transaction ReportMalicious(Address maliciousMiningAddress, UInt256 blockNumber, byte[] proof) => GenerateTransaction<GeneratedTransaction>(Definition.GetFunction(nameof(ReportMalicious)), _nodeAddress, maliciousMiningAddress, blockNumber, proof);

        /// <summary>
        /// Reports that the benign validator misbehaved at the specified block.
        /// Called by the node of each honest validator after the specified validator misbehaved.
        /// <seealso>
        ///     <cref>https://openethereum.github.io/wiki/Validator-Set.html#reporting-contract</cref>
        /// </seealso>
        /// </summary>
        /// <param name="maliciousMiningAddress">The mining address of the malicious validator.</param>
        /// <param name="blockNumber">The block number where the misbehavior was observed.</param>
        /// <returns>Transaction to be added to pool.</returns>
        public Transaction ReportBenign(Address maliciousMiningAddress, UInt256 blockNumber) => GenerateTransaction<GeneratedTransaction>(Definition.GetFunction(nameof(ReportBenign)), _nodeAddress, maliciousMiningAddress, blockNumber);
    }
}