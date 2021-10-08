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
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Nethermind.Cryptography;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Microsoft.Extensions.Options;
using Nethermind.Core2.Cryptography.Ssz;
using Nethermind.Dirichlet.Numerics;
using Nethermind.Merkleization;
using Nethermind.Ssz;

namespace Nethermind.Core2.Cryptography
{
    /// <summary>
    /// Implementation of ICryptographyService that uses the Cortex BLS nuget package
    /// </summary>
    public class CryptographyService : ICryptographyService
    {
        private readonly BLS _bls;
        private readonly ChainConstants _chainConstants;
        private readonly IOptionsMonitor<MiscellaneousParameters> _miscellaneousParameterOptions;
        private readonly IOptionsMonitor<TimeParameters> _timeParameterOptions;
        private readonly IOptionsMonitor<StateListLengths> _stateListLengthOptions;
        private readonly IOptionsMonitor<MaxOperationsPerBlock> _maxOperationsPerBlockOptions;

        public CryptographyService(ChainConstants chainConstants,
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

            MiscellaneousParameters miscellaneousParameters = miscellaneousParameterOptions.CurrentValue;
            TimeParameters timeParameters = timeParameterOptions.CurrentValue;
            StateListLengths stateListLengths = stateListLengthOptions.CurrentValue;
            MaxOperationsPerBlock maxOperationsPerBlock = maxOperationsPerBlockOptions.CurrentValue;
            
            BLSParameters blsParameters = new BLSParameters();
            _bls = BLS.Create(blsParameters);
            
            Nethermind.Ssz.Ssz.Init(
                chainConstants.DepositContractTreeDepth, 
                chainConstants.JustificationBitsLength,
                miscellaneousParameters.MaximumValidatorsPerCommittee,
                timeParameters.SlotsPerEpoch,
                timeParameters.SlotsPerEth1VotingPeriod,
                timeParameters.SlotsPerHistoricalRoot,
                stateListLengths.EpochsPerHistoricalVector,
                stateListLengths.EpochsPerSlashingsVector,
                stateListLengths.HistoricalRootsLimit,
                stateListLengths.ValidatorRegistryLimit,
                maxOperationsPerBlock.MaximumProposerSlashings,
                maxOperationsPerBlock.MaximumAttesterSlashings,
                maxOperationsPerBlock.MaximumAttestations,
                maxOperationsPerBlock.MaximumDeposits,
                maxOperationsPerBlock.MaximumVoluntaryExits);
            
            Nethermind.Ssz.Ssz.Init(
                chainConstants.DepositContractTreeDepth, 
                chainConstants.JustificationBitsLength,
                miscellaneousParameters.MaximumValidatorsPerCommittee,
                timeParameters.SlotsPerEpoch,
                timeParameters.SlotsPerEth1VotingPeriod,
                timeParameters.SlotsPerHistoricalRoot,
                stateListLengths.EpochsPerHistoricalVector,
                stateListLengths.EpochsPerSlashingsVector,
                stateListLengths.HistoricalRootsLimit,
                stateListLengths.ValidatorRegistryLimit,
                maxOperationsPerBlock.MaximumProposerSlashings,
                maxOperationsPerBlock.MaximumAttesterSlashings,
                maxOperationsPerBlock.MaximumAttestations,
                maxOperationsPerBlock.MaximumDeposits,
                maxOperationsPerBlock.MaximumVoluntaryExits);
        }
        
        
        private static readonly HashAlgorithm s_hashAlgorithm = SHA256.Create();

        public Func<BLSParameters, BLS> SignatureAlgorithmFactory { get; set; } = blsParameters => BLS.Create(blsParameters);

        public BlsPublicKey BlsAggregatePublicKeys(IList<BlsPublicKey> publicKeys)
        {
            Span<byte> publicKeysSpan = new Span<byte>(new byte[publicKeys.Count() * BlsPublicKey.Length]);
            int publicKeysSpanIndex = 0;
            foreach (BlsPublicKey publicKey in publicKeys)
            {
                publicKey.AsSpan().CopyTo(publicKeysSpan.Slice(publicKeysSpanIndex));
                publicKeysSpanIndex += BlsPublicKey.Length;
            }
            byte[] aggregatePublicKey = new byte[BlsPublicKey.Length];
            bool success = _bls.TryAggregatePublicKeys(publicKeysSpan, aggregatePublicKey, out int bytesWritten);
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

            return _bls.AggregateVerifyData(publicKeysSpan, signingRootsSpan, signature.AsSpan());
        }

        public bool BlsFastAggregateVerify(IList<BlsPublicKey> publicKeys, Root signingRoot, BlsSignature signature)
        {
            List<byte[]> publicKeysList = new List<byte[]>();
            foreach (BlsPublicKey publicKey in publicKeys)
            {
                publicKeysList.Add(publicKey.Bytes);
            }

            return _bls.FastAggregateVerifyData(publicKeysList, signingRoot.AsSpan(), signature.AsSpan());
        }

        public bool BlsVerify(BlsPublicKey publicKey, Root signingRoot, BlsSignature signature)
        {
            return _bls.VerifyData(publicKey.AsSpan(), signingRoot.AsSpan(), signature.AsSpan());
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
            byte[] result = new byte[Bytes32.Length];
            bool success = s_hashAlgorithm.TryComputeHash(bytes, result, out int bytesWritten);
            if (!success || bytesWritten != Bytes32.Length)
            {
                throw new Exception("Error generating hash value.");
            }
            return Bytes32.Wrap(result);
        }

        Root ICryptographyService.HashTreeRoot(AttestationData attestationData)
        {
            Merkle.Ize(out UInt256 root, attestationData);
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
            return new Root(bytes);
        }

        Root ICryptographyService.HashTreeRoot(BeaconBlock beaconBlock)
        {
            MiscellaneousParameters miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            MaxOperationsPerBlock maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;
            return beaconBlock.HashTreeRoot(maxOperationsPerBlock.MaximumProposerSlashings,
                maxOperationsPerBlock.MaximumAttesterSlashings, maxOperationsPerBlock.MaximumAttestations,
                maxOperationsPerBlock.MaximumDeposits, maxOperationsPerBlock.MaximumVoluntaryExits,
                miscellaneousParameters.MaximumValidatorsPerCommittee);
        }

        Root ICryptographyService.HashTreeRoot(BeaconBlockBody beaconBlockBody)
        {
            MiscellaneousParameters miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            MaxOperationsPerBlock maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;
            return beaconBlockBody.HashTreeRoot(maxOperationsPerBlock.MaximumProposerSlashings,
                maxOperationsPerBlock.MaximumAttesterSlashings, maxOperationsPerBlock.MaximumAttestations,
                maxOperationsPerBlock.MaximumDeposits, maxOperationsPerBlock.MaximumVoluntaryExits,
                miscellaneousParameters.MaximumValidatorsPerCommittee);
            
            // TODO: Get this working and switch over block body, beacon block, and state

//            Merkle.Ize(out UInt256 root, beaconBlockBody);
//            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
//            return new Hash32(bytes);
        }

        public Root HashTreeRoot(BeaconBlockHeader beaconBlockHeader)
        {
            return beaconBlockHeader.HashTreeRoot();
        }

        Root ICryptographyService.HashTreeRoot(BeaconState beaconState)
        {
            MiscellaneousParameters miscellaneousParameters = _miscellaneousParameterOptions.CurrentValue;
            TimeParameters timeParameters =  _timeParameterOptions.CurrentValue;
            StateListLengths stateListLengths =  _stateListLengthOptions.CurrentValue;
            MaxOperationsPerBlock maxOperationsPerBlock = _maxOperationsPerBlockOptions.CurrentValue;
            ulong maximumAttestationsPerEpoch = maxOperationsPerBlock.MaximumAttestations * (ulong) timeParameters.SlotsPerEpoch;
            return beaconState.HashTreeRoot(stateListLengths.HistoricalRootsLimit,
                timeParameters.SlotsPerEth1VotingPeriod, stateListLengths.ValidatorRegistryLimit,
                maximumAttestationsPerEpoch, miscellaneousParameters.MaximumValidatorsPerCommittee);

//            Merkle.Ize(out UInt256 root, beaconState);
//            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
//            return new Hash32(bytes);
        }

        Root ICryptographyService.HashTreeRoot(DepositData depositData)
        {
            Merkle.Ize(out UInt256 root, depositData);
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
            return new Root(bytes);
        }

        public Root HashTreeRoot(DepositMessage depositMessage)
        {
            Merkle.Ize(out UInt256 root, depositMessage);
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
            return new Root(bytes);
        }

        Root ICryptographyService.HashTreeRoot(List<DepositData> depositData)
        {
            Merkle.Ize(out UInt256 root, depositData);
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
            return new Root(bytes);
        }
        
        Root ICryptographyService.HashTreeRoot(List<Ref<DepositData>> depositData)
        {
            Merkle.Ize(out UInt256 root, depositData);
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
            return new Root(bytes);
        }

        Root ICryptographyService.HashTreeRoot(Epoch epoch)
        {
            Merkle.Ize(out UInt256 root, epoch);
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
            return new Root(bytes);
        }

        Root ICryptographyService.HashTreeRoot(HistoricalBatch historicalBatch)
        {
            Merkle.Ize(out UInt256 root, historicalBatch);
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
            return new Root(bytes);
        }

        public Root HashTreeRoot(SigningRoot signingRoot)
        {
            return signingRoot.HashTreeRoot();
            // Merkle.Ize(out UInt256 root, signingRoot);
            // Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
            // return new Root(bytes);
        }

        public Root HashTreeRoot(VoluntaryExit voluntaryExit)
        {
            Merkle.Ize(out UInt256 root, voluntaryExit);
            Span<byte> bytes = MemoryMarshal.Cast<UInt256, byte>(MemoryMarshal.CreateSpan(ref root, 1));
            return new Root(bytes);
        }
    }
}
