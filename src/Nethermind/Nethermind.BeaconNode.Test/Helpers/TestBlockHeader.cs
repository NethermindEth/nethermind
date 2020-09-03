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
using Nethermind.Core2;
using Nethermind.Core2.Configuration;
using Nethermind.Core2.Containers;
using Nethermind.Core2.Crypto;
using Nethermind.Core2.Cryptography.Ssz;
using Nethermind.Core2.Types;
namespace Nethermind.BeaconNode.Test.Helpers
{
    public static class TestBlockHeader
    {
        public static SignedBeaconBlockHeader SignBlockHeader(IServiceProvider testServiceProvider, BeaconState state, BeaconBlockHeader header, byte[] privateKey)
        {
            SignatureDomains signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;
            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            BeaconStateAccessor beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();

            Root headerRoot = cryptographyService.HashTreeRoot(header);
            Domain domain = beaconStateAccessor.GetDomain(state, signatureDomains.BeaconProposer, Epoch.None);
            Root signingRoot = beaconChainUtility.ComputeSigningRoot(headerRoot, domain);
            BlsSignature signature = TestSecurity.BlsSign(signingRoot, privateKey);
            
            return new SignedBeaconBlockHeader(header, signature);
        }
    }
}
