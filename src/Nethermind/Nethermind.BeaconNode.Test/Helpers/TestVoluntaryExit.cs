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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nethermind.Core2.Configuration;
using Nethermind.BeaconNode.Ssz;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Types;
namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestVoluntaryExit
    {
        public static VoluntaryExit BuildVoluntaryExit(IServiceProvider testServiceProvider, BeaconState state, Epoch epoch, ValidatorIndex validatorIndex, byte[] privateKey, bool signed)
        {
            var voluntaryExit = new VoluntaryExit(epoch, validatorIndex, BlsSignature.Empty);
            if (signed)
            {
                SignVoluntaryExit(testServiceProvider, state, voluntaryExit, privateKey);
            }
            return voluntaryExit;
        }

        public static void SignVoluntaryExit(IServiceProvider testServiceProvider, BeaconState state, VoluntaryExit voluntaryExit, byte[] privateKey)
        {
            var signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var domain = beaconStateAccessor.GetDomain(state, signatureDomains.VoluntaryExit, voluntaryExit.Epoch);
            var signature = TestSecurity.BlsSign(voluntaryExit.SigningRoot(), privateKey, domain);
            voluntaryExit.SetSignature(signature);
        }
    }
}
