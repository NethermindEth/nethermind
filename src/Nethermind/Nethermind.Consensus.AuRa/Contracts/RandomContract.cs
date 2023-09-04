// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Abi;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Evm.TransactionProcessing;

namespace Nethermind.Consensus.AuRa.Contracts
{
    public interface IRandomContract : IActivatedAtBlock
    {
        (Phase Phase, UInt256 Round) GetPhase(BlockHeader parentHeader);

        /// <summary>
        /// Returns the Keccak-256 hash and cipher of the validator's secret for the specified collection round and the specified validator stored by the validator through the `commitHash` function.
        /// </summary>
        /// <param name="parentHeader">Block header on which this is to be executed on.</param>
        /// <param name="collectRound">The serial number of the collection round for which hash and cipher should be retrieved.</param>
        /// <returns>Keccak-256 hash and cipher of the validator's secret for the specified collection round and the specified validator stored by the validator through the `commitHash` function.</returns>
        /// <remarks>
        /// The mining address of validator is last contract parameter.
        /// </remarks>
        (Keccak Hash, byte[] Cipher) GetCommitAndCipher(BlockHeader parentHeader, in UInt256 collectRound);

        /// <summary>
        /// Called by the validator's node to store a hash and a cipher of the validator's secret on each collection round.
        /// The validator's node must use its mining address to call this function.
        /// This function can only be called once per collection round (during the `commits phase`).
        /// </summary>
        /// <param name="secretHash">The Keccak-256 hash of the validator's secret.</param>
        /// <param name="cipher">The cipher of the validator's secret. Can be used by the node to restore the lost secret after the node is restarted (see the `getCipher` getter).</param>
        /// <returns>Transaction to be included in block.</returns>
        Transaction CommitHash(in Keccak secretHash, byte[] cipher);

        /// <summary>
        /// Called by the validator's node to XOR its number with the current random seed.
        /// The validator's node must use its mining address to call this function.
        /// This function can only be called once per collection round (during the `reveals phase`).
        /// </summary>
        /// <param name="number">The validator's number.</param>
        /// <returns>Transaction to be included in block.</returns>
        Transaction RevealNumber(in UInt256 number);

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
    }

    public sealed class RandomContract : Blockchain.Contracts.Contract, IRandomContract
    {
        private readonly ISigner _signer;
        private IConstantContract Constant { get; }

        public RandomContract(
            IAbiEncoder abiEncoder,
            Address contractAddress,
            IReadOnlyTxProcessorSource readOnlyTxProcessorSource,
            long transitionBlock,
            ISigner signer)
            : base(abiEncoder, contractAddress ?? throw new ArgumentNullException(nameof(contractAddress)))
        {
            _signer = signer;
            Activation = transitionBlock;
            Constant = GetConstant(readOnlyTxProcessorSource);
        }

        public long Activation { get; }

        public (IRandomContract.Phase Phase, UInt256 Round) GetPhase(BlockHeader parentHeader)
        {
            this.BlockActivationCheck(parentHeader);

            UInt256 round = CurrentCollectRound(parentHeader);
            bool isCommitPhase = IsCommitPhase(parentHeader);
            bool isCommitted = IsCommitted(parentHeader, round);
            bool revealed = SentReveal(parentHeader, round);

            var phase = isCommitPhase
                ? revealed
                    ? throw new AuRaException("Revealed random number during commit phase.")
                    : !isCommitted
                        ? IRandomContract.Phase.BeforeCommit
                        : IRandomContract.Phase.Committed
                : !isCommitted // We apparently entered too late to make a commitment, wait until we get a chance again.
                  || revealed
                    ? IRandomContract.Phase.Waiting
                    : IRandomContract.Phase.Reveal;

            return (phase, round);
        }

        private Address SignerAddress => _signer.Address;

        /// <summary>
        /// Returns a boolean flag of whether the specified validator has revealed their number for the specified collection round.
        /// </summary>
        /// <param name="parentHeader">Block header on which this is to be executed on.</param>
        /// <param name="collectRound">The serial number of the collection round for which the checkup should be done.</param>
        /// <returns>Boolean flag of whether the specified validator has revealed their number for the specified collection round.</returns>
        /// <remarks>
        /// The mining address of validator is last contract parameter.
        /// </remarks>
        private bool SentReveal(BlockHeader parentHeader, in UInt256 collectRound) => Constant.Call<bool>(parentHeader, nameof(SentReveal), SignerAddress, collectRound, SignerAddress);

        /// <summary>
        /// Returns a boolean flag indicating whether the specified validator has committed their secret's hash for the specified collection round.
        /// </summary>
        /// <param name="parentHeader">Block header on which this is to be executed on.</param>
        /// <param name="collectRound">The serial number of the collection round for which the checkup should be done.</param>
        /// <returns>Boolean flag indicating whether the specified validator has committed their secret's hash for the specified collection round.</returns>
        /// <remarks>
        /// The mining address of validator is last contract parameter.
        /// </remarks>
        private bool IsCommitted(BlockHeader parentHeader, in UInt256 collectRound) => Constant.Call<bool>(parentHeader, nameof(IsCommitted), SignerAddress, collectRound, SignerAddress);

        /// <summary>
        /// Returns the serial number of the current collection round.
        /// </summary>
        /// <param name="parentHeader">Block header on which this is to be executed on.</param>
        /// <returns>Serial number of the current collection round.</returns>
        private UInt256 CurrentCollectRound(BlockHeader parentHeader) =>
            Constant.Call<UInt256>(parentHeader, nameof(CurrentCollectRound), SignerAddress);


        /// <summary>
        /// Returns a boolean flag indicating whether the current phase of the current collection round is a `commits phase`.
        /// Used by the validator's node to determine if it should commit the hash of the secret during the current collection round.
        /// </summary>
        /// <param name="parentHeader">Block header on which this is to be executed on.</param>
        /// <returns>Boolean flag indicating whether the current phase of the current collection round is a `commits phase`.</returns>
        private bool IsCommitPhase(BlockHeader parentHeader) => Constant.Call<bool>(parentHeader, nameof(IsCommitPhase), SignerAddress);

        /// <summary>
        /// Returns the Keccak-256 hash and cipher of the validator's secret for the specified collection round and the specified validator stored by the validator through the `commitHash` function.
        /// </summary>
        /// <param name="parentHeader">Block header on which this is to be executed on.</param>
        /// <param name="collectRound">The serial number of the collection round for which hash and cipher should be retrieved.</param>
        /// <returns>Keccak-256 hash and cipher of the validator's secret for the specified collection round and the specified validator stored by the validator through the `commitHash` function.</returns>
        /// <remarks>
        /// The mining address of validator is last contract parameter.
        /// </remarks>
        public (Keccak Hash, byte[] Cipher) GetCommitAndCipher(BlockHeader parentHeader, in UInt256 collectRound)
        {
            var (hash, cipher) = Constant.Call<byte[], byte[]>(parentHeader, nameof(GetCommitAndCipher), SignerAddress, collectRound, SignerAddress);
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
        public Transaction CommitHash(in Keccak secretHash, byte[] cipher) => GenerateTransaction<GeneratedTransaction>(nameof(CommitHash), SignerAddress, secretHash.BytesToArray(), cipher);

        /// <summary>
        /// Called by the validator's node to XOR its number with the current random seed.
        /// The validator's node must use its mining address to call this function.
        /// This function can only be called once per collection round (during the `reveals phase`).
        /// </summary>
        /// <param name="number">The validator's number.</param>
        /// <returns>Transaction to be included in block.</returns>
        public Transaction RevealNumber(in UInt256 number) => GenerateTransaction<GeneratedTransaction>(nameof(RevealNumber), SignerAddress, number);
    }
}
