// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
