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
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using Nethermind.Cryptography;
using Microsoft.Extensions.Options;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Cryptography.Ssz;
using Nethermind.Core2.Types;

namespace Nethermind.Core2.Cryptography
{
    /// <summary>
    /// Implementation of ICryptographyService that uses the Cortex nuget packages (BLS and SSZ)
    /// </summary>
    [Obsolete("Use CryptographyService, which has Cortex BLS with Nethermind SSZ")]
    public class CortexCryptographyService : ICryptographyService
    {
        private readonly ChainConstants _chainConstants;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;


        private static readonly HashAlgorithm s_hashAlgorithm = SHA256.Create();

        public CortexCryptographyService(ChainConstants chainConstants,
            IOptionsMonitor<MiscellaneousParameters> miscellaneousParameterOptions,
            IOptionsMonitor<TimeParameters> timeParameterOptions,
            IOptionsMonitor<StateListLengths> stateListLengthOptions,
            IOptionsMonitor<MaxOperationsPerBlock> maxOperationsPerBlockOptions)
        {
            _chainConstants = chainConstants;
            _miscellaneousParameterOptions = miscellaneousParameterOptions;
            _timeParameterOptions = timeParameterOptions;
            _stateListLengthOptions = stateListLengthOptions;
            _maxOperationsPerBlockOptions = maxOperationsPerBlockOptions;
        }

        public Func<BLSParameters, BLS> SignatureAlgorithmFactory { get; set; } =
            blsParameters => BLS.Create(blsParameters);

        public BlsPublicKey BlsAggregatePublicKeys(IList<BlsPublicKey> publicKeys)
        {
            Span<byte> publicKeysSpan = new Span<byte>(new byte[publicKeys.Count() * BlsPublicKey.Length]);
            int publicKeysSpanIndex = 0;
            foreach (BlsPublicKey publicKey in publicKeys)
            {
                publicKey.AsSpan().CopyTo(publicKeysSpan.Slice(publicKeysSpanIndex));
                publicKeysSpanIndex += BlsPublicKey.Length;
            }

            using BLS signatureAlgorithm = SignatureAlgorithmFactory(new BLSParameters());
            byte[] aggregatePublicKey = new byte[BlsPublicKey.Length];
            bool success =
                signatureAlgorithm.TryAggregatePublicKeys(publicKeysSpan, aggregatePublicKey, out int bytesWritten);
            if (!success || bytesWritten != BlsPublicKey.Length)
            {
                throw new Exception("Error generating aggregate public key.");
            }

            return new BlsPublicKey(aggregatePublicKey);
        }

        public bool BlsAggregateVerify(IList<BlsPublicKey> publicKeys, IList<Root> signingRoots, BlsSignature signature)
        {
            int count = publicKeys.Count();

            Span<byte> publicKeysSpan = new Span<byte>(new byte[count * BlsPublicKey.Length]);
            int publicKeysSpanIndex = 0;
            foreach (BlsPublicKey publicKey in publicKeys)
            {
                publicKey.AsSpan().CopyTo(publicKeysSpan.Slice(publicKeysSpanIndex));
                publicKeysSpanIndex += BlsPublicKey.Length;
            }

            Span<byte> signingRootsSpan = new Span<byte>(new byte[count * Root.Length]);
            int signingRootsSpanIndex = 0;
            foreach (Root signingRoot in signingRoots)
            {
                signingRoot.AsSpan().CopyTo(signingRootsSpan.Slice(signingRootsSpanIndex));
                signingRootsSpanIndex += Root.Length;
            }

            using BLS signatureAlgorithm = SignatureAlgorithmFactory(new BLSParameters());
            return signatureAlgorithm.AggregateVerifyHashes(publicKeysSpan, signingRootsSpan, signature.AsSpan());
        }

        public bool BlsFastAggregateVerify(IList<BlsPublicKey> publicKey, Root signingRoot, BlsSignature signature)
        {
            throw new NotImplementedException();
        }

        public bool BlsVerify(BlsPublicKey publicKey, Root signingRoot, BlsSignature signature)
        {
            BLSParameters blsParameters = new BLSParameters() {PublicKey = publicKey.AsSpan().ToArray()};
            using BLS signatureAlgorithm = SignatureAlgorithmFactory(blsParameters);
            return signatureAlgorithm.VerifyHash(signingRoot.AsSpan(), signature.AsSpan());
        }

        public Bytes32 Hash(Bytes32 a, Bytes32 b)
        {
            Span<byte> input = new Span<byte>(new byte[Bytes32.Length * 2]);
            a.AsSpan().CopyTo(input);
            b.AsSpan().CopyTo(input.Slice(Bytes32.Length));
            return Hash(input);
        }

        public Bytes32 Hash(ReadOnlySpan<byte> bytes)
        {
            Span<byte> result = new Span<byte>(new byte[Bytes32.Length]);
            bool success = s_hashAlgorithm.TryComputeHash(bytes, result, out int bytesWritten);
            if (!success || bytesWritten != Bytes32.Length)
            {
                throw new Exception("Error generating hash value.");
            }

            return new Bytes32(result);
        }

        public Root HashTreeRoot(AttestationData attestationData)
        {
            return attestationData.HashTreeRoot();
        }

        public Root HashTreeRoot(BeaconBlock beaconBlock)
        {
            MiscellaneousParameters miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            MaxOperationsPerBlock maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;
            return beaconBlock.HashTreeRoot(maxOperationsPerBlock.MaximumProposerSlashings,
                maxOperationsPerBlock.MaximumAttesterSlashings, maxOperationsPerBlock.MaximumAttestations,
                maxOperationsPerBlock.MaximumDeposits, maxOperationsPerBlock.MaximumVoluntaryExits,
                miscellaneousParameters.MaximumValidatorsPerCommittee);
        }

        public Root HashTreeRoot(BeaconBlockBody beaconBlockBody)
        {
            MiscellaneousParameters miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            MaxOperationsPerBlock maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;
            return beaconBlockBody.HashTreeRoot(maxOperationsPerBlock.MaximumProposerSlashings,
                maxOperationsPerBlock.MaximumAttesterSlashings, maxOperationsPerBlock.MaximumAttestations,
                maxOperationsPerBlock.MaximumDeposits, maxOperationsPerBlock.MaximumVoluntaryExits,
                miscellaneousParameters.MaximumValidatorsPerCommittee);
        }

        public Root HashTreeRoot(List<Ref<DepositData>> depositData)
        {
            throw new InvalidOperationException();
            // return depositData.HashTreeRoot(_chainConstants.MaximumDepositContracts);
        }

        public Root HashTreeRoot(List<DepositData> depositData)
        {
            throw new NotImplementedException();
        }

        public Root HashTreeRoot(Epoch epoch)
        {
            return epoch.HashTreeRoot();
        }

        public Root HashTreeRoot(HistoricalBatch historicalBatch)
        {
            return historicalBatch.HashTreeRoot();
        }

        public Root HashTreeRoot(SigningRoot signingRoot)
        {
            return signingRoot.HashTreeRoot();
        }

        public Root HashTreeRoot(BeaconState beaconState)
        {
            MiscellaneousParameters miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            TimeParameters timeParameters = _timeParameterOptions.CurrentValue;
            StateListLengths stateListLengths = _stateListLengthOptions.CurrentValue;
            MaxOperationsPerBlock maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;
            ulong maximumAttestationsPerEpoch =
                maxOperationsPerBlock.MaximumAttestations * timeParameters.SlotsPerEpoch;
            return beaconState.HashTreeRoot(stateListLengths.HistoricalRootsLimit,
                timeParameters.SlotsPerEth1VotingPeriod, stateListLengths.ValidatorRegistryLimit,
                maximumAttestationsPerEpoch, miscellaneousParameters.MaximumValidatorsPerCommittee);
        }

        public Root HashTreeRoot(DepositData depositData)
        {
            return depositData.HashTreeRoot();
        }

        public Root HashTreeRoot(BeaconBlockHeader beaconBlockHeader)
        {
            return beaconBlockHeader.HashTreeRoot();
        }

        public Root HashTreeRoot(DepositMessage depositMessage)
        {
            return depositMessage.HashTreeRoot();
        }

        public Root HashTreeRoot(VoluntaryExit voluntaryExit)
        {
            return voluntaryExit.HashTreeRoot();
        }
    }
}