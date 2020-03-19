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
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
using Shouldly;
namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestAttesterSlashing
    {
        public static AttesterSlashing GetValidAttesterSlashing(IServiceProvider testServiceProvider, BeaconState state, bool signed1, bool signed2)
        {
            TimeParameters timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            Attestation attestation1 = TestAttestation.GetValidAttestation(testServiceProvider, state, Slot.None, CommitteeIndex.None, signed1);

            Root targetRoot2 = new Root(Enumerable.Repeat((byte) 0x01, 32).ToArray());
            Attestation attestation2 = new Attestation(
                attestation1.AggregationBits,
                new AttestationData(attestation1.Data.Slot,
                    attestation1.Data.Index,
                    attestation1.Data.BeaconBlockRoot,
                    attestation1.Data.Source,
                    new Checkpoint(
                        attestation1.Data.Target.Epoch,
                        targetRoot2
                        )), 
                BlsSignature.Zero
                );
            if (signed2)
            {
                TestAttestation.SignAttestation(testServiceProvider, state, attestation2);
            }

            IndexedAttestation indexedAttestation1 = beaconStateAccessor.GetIndexedAttestation(state, attestation1);
            IndexedAttestation indexedAttestation2 = beaconStateAccessor.GetIndexedAttestation(state, attestation2);

            AttesterSlashing attesterSlashing = new AttesterSlashing(indexedAttestation1, indexedAttestation2);
            return attesterSlashing;
        }
    }
}
