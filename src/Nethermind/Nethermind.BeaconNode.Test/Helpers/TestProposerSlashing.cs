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
