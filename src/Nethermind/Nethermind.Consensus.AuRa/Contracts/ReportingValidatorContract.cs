// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core;
using Nethermind.Experimental.Abi;
using Nethermind.Int256;

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
        Transaction ReportMalicious(Address maliciousMinerAddress, in UInt256 blockNumber, byte[] proof);

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
        Transaction ReportBenign(Address maliciousMinerAddress, in UInt256 blockNumber);
    }

    public sealed class ReportingValidatorContract(
        Address contractAddress,
        ISigner signer) : Contract(contractAddress ?? throw new ArgumentNullException(nameof(contractAddress))), IReportingValidatorContract
    {
        private static readonly AbiSignature<(Address, UInt256, byte[])> ReportMaliciousSignature = new AbiSignature("reportMalicious").With(AbiType.Address, AbiType.UInt256, AbiType.Bytes);
        private static readonly AbiSignature<(Address, UInt256)> ReportBenignSignature = new AbiSignature("reportBenign").With(AbiType.Address, AbiType.UInt256);

        private readonly ISigner _signer = signer ?? throw new ArgumentNullException(nameof(signer));
        public Address NodeAddress => _signer.Address;

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
        public Transaction ReportMalicious(Address maliciousMinerAddress, in UInt256 blockNumber, byte[] proof) => GenerateTransaction<GeneratedTransaction>(AbiCodec.Encode(ReportMaliciousSignature, (maliciousMinerAddress, blockNumber, proof)), NodeAddress);

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
        public Transaction ReportBenign(Address maliciousMinerAddress, in UInt256 blockNumber) => GenerateTransaction<GeneratedTransaction>(AbiCodec.Encode(ReportBenignSignature, (maliciousMinerAddress, blockNumber)), NodeAddress);
    }
}
