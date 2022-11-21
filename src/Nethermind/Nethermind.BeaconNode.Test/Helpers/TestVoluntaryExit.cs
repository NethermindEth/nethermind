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
    public static class TestVoluntaryExit
    {
        public static VoluntaryExit BuildVoluntaryExit(IServiceProvider testServiceProvider, Epoch epoch, ValidatorIndex validatorIndex)
        {
            var voluntaryExit = new VoluntaryExit(epoch, validatorIndex);
            return voluntaryExit;
        }

        public static SignedVoluntaryExit SignVoluntaryExit(IServiceProvider testServiceProvider, BeaconState state, VoluntaryExit voluntaryExit, byte[] privateKey)
        {
            var signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;
            IBeaconChainUtility beaconChainUtility = testServiceProvider.GetService<IBeaconChainUtility>();
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();
            ICryptographyService cryptographyService = testServiceProvider.GetService<ICryptographyService>();

            var voluntaryExitRoot = cryptographyService.HashTreeRoot(voluntaryExit);
            var domain = beaconStateAccessor.GetDomain(state, signatureDomains.VoluntaryExit, voluntaryExit.Epoch);
            var signingRoot = beaconChainUtility.ComputeSigningRoot(voluntaryExitRoot, domain);
            var signature = TestSecurity.BlsSign(signingRoot, privateKey);

            return new SignedVoluntaryExit(voluntaryExit, signature);
        }
    }
}
