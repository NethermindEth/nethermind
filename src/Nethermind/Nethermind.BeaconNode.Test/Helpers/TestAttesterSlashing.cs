// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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

            Root targetRoot2 = new Root(Enumerable.Repeat((byte)0x01, 32).ToArray());
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
