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
using Nethermind.Core2;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;

namespace Nethermind.Ssz.Benchmarks
{
    [CoreJob]
    [MemoryDiagnoser]
    public class SszBeaconBlockBodyBenchmark
    {
        private BeaconBlockBody _body = new BeaconBlockBody();
        private byte[] _encoded;
        
        public SszBeaconBlockBodyBenchmark()
        {
            AttestationData data = new AttestationData(
                new Slot(1),
                new CommitteeIndex(4),
                Sha256.OfAnEmptyString,
                new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString),
                new Checkpoint(new Epoch(3), Sha256.OfAnEmptyString));
            
            Attestation attestation = new Attestation(
                new BitArray(new byte[5]),
                data,
                SszTest.TestSig1
                );

            DepositData depositData = new DepositData(
                SszTest.TestKey1,
                Sha256.OfAnEmptyString,
                new Gwei(7),
                SszTest.TestSig1);

            Deposit deposit = new Deposit(
                new Hash32[ByteLength.ContractTreeDepth + 1],
                depositData);

            IndexedAttestation indexedAttestation1 = new IndexedAttestation(
                new ValidatorIndex[8],
                data,
                SszTest.TestSig1);

            IndexedAttestation indexedAttestation2 = new IndexedAttestation(
                new ValidatorIndex[8],
                data,
                SszTest.TestSig1);

            AttesterSlashing slashing = new AttesterSlashing(indexedAttestation1, indexedAttestation2);

            Eth1Data eth1Data = new Eth1Data(
                Sha256.OfAnEmptyString,
                9,
                Sha256.OfAnEmptyString);
            
            _body.Attestations = new Attestation[3];
            _body.Attestations[1] = attestation;
            
            _body.Deposits = new Deposit[3];
            _body.Deposits[2] = deposit;
            
            _body.Graffiti = new byte[32];
            _body.AttesterSlashings = new AttesterSlashing[3];
            _body.AttesterSlashings[0] = slashing;
            _body.Eth1Data = eth1Data;
            _body.ProposerSlashings = new ProposerSlashing[10];
            _body.RandaoReversal = SszTest.TestSig1;
            _body.VoluntaryExits = new VoluntaryExit[11];

            _encoded = new byte[BeaconBlockBody.SszLength(_body)];
        }
        
        [Benchmark(Baseline = true)]
        public void Current()
        {
            Ssz.Encode(_encoded, _body);
        }
    }
}