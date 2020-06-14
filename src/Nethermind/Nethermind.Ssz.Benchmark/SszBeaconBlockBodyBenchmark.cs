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

using System.Collections;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz.Benchmarks
{
    [SimpleJob(RuntimeMoniker.NetCoreApp31)]
    [MemoryDiagnoser]
    public class SszBeaconBlockBodyBenchmark
    {
        public static BlsPublicKey TestKey1 = new BlsPublicKey(
            "0x000102030405060708090a0b0c0d0e0f" +
            "101112131415161718191a1b1c1d1e1f" +
            "202122232425262728292a2b2c2d2e2f");

        public static BlsSignature TestSig1 = new BlsSignature(new byte[BlsSignature.Length]);
        
        private BeaconBlockBody _body;
        private byte[] _encoded;
        
        public SszBeaconBlockBodyBenchmark()
        {
            AttestationData data = new AttestationData(
                new Slot(1),
                new CommitteeIndex(4),
                Sha256.RootOfAnEmptyString,
                new Checkpoint(new Epoch(2), Sha256.RootOfAnEmptyString),
                new Checkpoint(new Epoch(3), Sha256.RootOfAnEmptyString));
            
            Attestation attestation = new Attestation(
                new BitArray(new byte[5]),
                data,
                TestSig1
                );

            DepositData depositData = new DepositData(
                TestKey1,
                Sha256.Bytes32OfAnEmptyString,
                new Gwei(7),
                TestSig1);

            Deposit deposit = new Deposit(
                new Bytes32[Ssz.DepositContractTreeDepth + 1],
                depositData.OrRoot);

            IndexedAttestation indexedAttestation1 = new IndexedAttestation(
                new ValidatorIndex[8],
                data,
                TestSig1);

            IndexedAttestation indexedAttestation2 = new IndexedAttestation(
                new ValidatorIndex[8],
                data,
                TestSig1);

            AttesterSlashing slashing = new AttesterSlashing(indexedAttestation1, indexedAttestation2);

            Eth1Data eth1Data = new Eth1Data(
                Sha256.RootOfAnEmptyString,
                9,
                Sha256.Bytes32OfAnEmptyString);
            
            Attestation[] attestations = new Attestation[3];
            attestations[1] = attestation;
            
            Deposit[] deposits = new Deposit[3];
            deposits[2] = deposit;
            
            Bytes32 graffiti = new Bytes32(new byte[32]);
            
            AttesterSlashing[] attesterSlashings = new AttesterSlashing[3];
            attesterSlashings[0] = slashing;
            
            ProposerSlashing[] proposerSlashings = new ProposerSlashing[10];

            BlsSignature randaoReveal = TestSig1;
            
            SignedVoluntaryExit[] signedVoluntaryExits = new SignedVoluntaryExit[11];

            _body = new BeaconBlockBody(randaoReveal,
                eth1Data,
                graffiti,
                proposerSlashings,
                attesterSlashings,
                attestations,
                deposits,
                signedVoluntaryExits);

            _encoded = new byte[Ssz.BeaconBlockBodyLength(_body)];
        }
        
        [Benchmark(Baseline = true)]
        public void Current()
        {
            Ssz.Encode(_encoded, _body);
        }
        
        [Benchmark(Baseline = true)]
        public void Cortex()
        {
            
            Ssz.Encode(_encoded, _body);
        }
    }
}