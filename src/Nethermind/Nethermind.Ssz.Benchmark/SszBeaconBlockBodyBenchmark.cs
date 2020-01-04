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
            AttestationData data = new AttestationData();
            data.Slot = new Slot(1);
            data.Source = new Checkpoint(new Epoch(2), Sha256.OfAnEmptyString);
            data.Target = new Checkpoint(new Epoch(3), Sha256.OfAnEmptyString);
            data.CommitteeIndex = new CommitteeIndex(4);
            data.BeaconBlockRoot = Sha256.OfAnEmptyString;
            
            Attestation attestation = new Attestation();
            attestation.Data = data;
            attestation.Signature = BlsSignature.TestSig1;
            attestation.AggregationBits = new byte[5];

            DepositData depositData = new DepositData();
            depositData.Amount = new Gwei(7);
            depositData.Signature = BlsSignature.TestSig1;
            depositData.PublicKey = SszTest.TestKey1;
            depositData.WithdrawalCredentials = Sha256.OfAnEmptyString;
            
            Deposit deposit = new Deposit();
            deposit.Data = depositData;
            deposit.Proof = new Hash32[Deposit.ContractTreeDepth + 1];
            
            IndexedAttestation indexedAttestation1 = new IndexedAttestation();
            indexedAttestation1.Data = data;
            indexedAttestation1.Signature = BlsSignature.TestSig1;
            indexedAttestation1.AttestingIndices = new ValidatorIndex[8];

            IndexedAttestation indexedAttestation2 = new IndexedAttestation();
            indexedAttestation2.Data = data;
            indexedAttestation2.Signature = BlsSignature.TestSig1;
            indexedAttestation2.AttestingIndices = new ValidatorIndex[8];

            AttesterSlashing slashing = new AttesterSlashing();
            slashing.Attestation1 = indexedAttestation1;
            slashing.Attestation2 = indexedAttestation2;

            Eth1Data eth1Data = new Eth1Data();
            eth1Data.BlockHash = Sha256.OfAnEmptyString;
            eth1Data.DepositCount = 9;
            eth1Data.DepositRoot = Sha256.OfAnEmptyString;
            
            _body.Attestations = new Attestation[3];
            _body.Attestations[1] = attestation;
            
            _body.Deposits = new Deposit[3];
            _body.Deposits[2] = deposit;
            
            _body.Graffiti = new byte[32];
            _body.AttesterSlashings = new AttesterSlashing[3];
            _body.AttesterSlashings[0] = slashing;
            _body.Eth1Data = eth1Data;
            _body.ProposerSlashings = new ProposerSlashing[10];
            _body.RandaoReversal = BlsSignature.TestSig1;
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