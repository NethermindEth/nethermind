//  Copyright (c) 2021 Demerzel Solutions Limited
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
using Nethermind.Abi;
using Nethermind.Blockchain.Contracts;
using Nethermind.Blockchain.Contracts.Json;
using Nethermind.Core;
using Nethermind.Int256;
using Nethermind.Evm;
using Nethermind.State;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface IReportingValidatorContract
    {
        Address NodeAddress { get; }

        /// <summary>
        /// Reports that the malicious validator misbehaved at the specified block.
        /// Called by the node of each honest validator after the specified validator misbehaved.
        /// <seealso>
        ///     <cref>https://openethereum.github.io/wiki/Validator-Set.html#reporting-contract</cref>
        /// </seealso>
        /// </summary>
        /// <param name="maliciousMinerAddress">The mining address of the malicious validator.</param>
        /// <param name="blockNumber">The block number where the misbehavior was observed.</param>
        /// <param name="proof">Proof of misbehavior.</param>
        /// <returns>Transaction to be added to pool.</returns>
        Transaction ReportMalicious(Address maliciousMinerAddress, UInt256 blockNumber, byte[] proof);

        /// <summary>
        /// Reports that the benign validator misbehaved at the specified block.
        /// Called by the node of each honest validator after the specified validator misbehaved.
        /// <seealso>
        ///     <cref>https://openethereum.github.io/wiki/Validator-Set.html#reporting-contract</cref>
        /// </seealso>
        /// </summary>
        /// <param name="maliciousMinerAddress">The mining address of the malicious validator.</param>
        /// <param name="blockNumber">The block number where the misbehavior was observed.</param>
        /// <returns>Transaction to be added to pool.</returns>
        Transaction ReportBenign(Address maliciousMinerAddress, UInt256 blockNumber);
    }

    public sealed class ReportingValidatorContract : Contract, IReportingValidatorContract
    {
        private readonly ISigner _signer;
        public Address NodeAddress => _signer.Address;
        
        public ReportingValidatorContract(
            IAbiEncoder abiEncoder, 
            Address contractAddress,
            ISigner signer)
            : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
            _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        }

        /// <summary>
        /// Reports that the malicious validator misbehaved at the specified block.
        /// Called by the node of each honest validator after the specified validator misbehaved.
        /// <seealso>
        ///     <cref>https://openethereum.github.io/wiki/Validator-Set.html#reporting-contract</cref>
        /// </seealso>
        /// </summary>
        /// <param name="maliciousMinerAddress">The mining address of the malicious validator.</param>
        /// <param name="blockNumber">The block number where the misbehavior was observed.</param>
        /// <param name="proof">Proof of misbehavior.</param>
        /// <returns>Transaction to be added to pool.</returns>
        public Transaction ReportMalicious(Address maliciousMinerAddress, UInt256 blockNumber, byte[] proof) => GenerateTransaction<GeneratedTransaction>(nameof(ReportMalicious), NodeAddress, maliciousMinerAddress, blockNumber, proof);

        /// <summary>
        /// Reports that the benign validator misbehaved at the specified block.
        /// Called by the node of each honest validator after the specified validator misbehaved.
        /// <seealso>
        ///     <cref>https://openethereum.github.io/wiki/Validator-Set.html#reporting-contract</cref>
        /// </seealso>
        /// </summary>
        /// <param name="maliciousMinerAddress">The mining address of the malicious validator.</param>
        /// <param name="blockNumber">The block number where the misbehavior was observed.</param>
        /// <returns>Transaction to be added to pool.</returns>
        public Transaction ReportBenign(Address maliciousMinerAddress, UInt256 blockNumber) => GenerateTransaction<GeneratedTransaction>(nameof(ReportBenign), NodeAddress, maliciousMinerAddress, blockNumber);
    }
}
