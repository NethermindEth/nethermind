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
namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestProposerSlashing
    {
        public static ProposerSlashing GetValidProposerSlashing(IServiceProvider testServiceProvider, BeaconState state, bool signed1, bool signed2)
        {
            var timeParameters = testServiceProvider.GetService<IOptions<TimeParameters>>().Value;
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var currentEpoch = beaconStateAccessor.GetCurrentEpoch(state);
            var validatorIndex = beaconStateAccessor.GetActiveValidatorIndices(state, currentEpoch).Last();
            var validator = state.Validators[(int)(ulong)validatorIndex];
            var privateKey = TestKeys.PublicKeyToPrivateKey(validator.PublicKey, timeParameters);
            var slot = state.Slot;

            var header1 = new BeaconBlockHeader(
                slot,
                new Root(Enumerable.Repeat((byte)0x33, 32).ToArray()),
                new Root(Enumerable.Repeat((byte)0x44, 32).ToArray()),
                new Root(Enumerable.Repeat((byte)0x45, 32).ToArray())
                );

            var header2 = new BeaconBlockHeader(
                slot,
                new Root(Enumerable.Repeat((byte)0x99, 32).ToArray()),
                new Root(Enumerable.Repeat((byte)0x44, 32).ToArray()),
                new Root(Enumerable.Repeat((byte)0x45, 32).ToArray())
                );

            SignedBeaconBlockHeader signedHeader1 = new SignedBeaconBlockHeader(header1, BlsSignature.Zero);
            if (signed1)
            {
                signedHeader1 = TestBlockHeader.SignBlockHeader(testServiceProvider, state, header1, privateKey);
            }

            SignedBeaconBlockHeader signedHeader2 = new SignedBeaconBlockHeader(header2, BlsSignature.Zero);
            if (signed2)
            {
                signedHeader2 = TestBlockHeader.SignBlockHeader(testServiceProvider, state, header2, privateKey);
            }

            var proposerSlashing = new ProposerSlashing(validatorIndex, signedHeader1, signedHeader2);

            return proposerSlashing;
        }
    }
}
