﻿using System;
using Cortex.BeaconNode.Configuration;
using Cortex.BeaconNode.Ssz;
using Cortex.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Cortex.BeaconNode.Tests.Helpers
{
    public static class TestBlockHeader
    {
        public static void SignBlockHeader(IServiceProvider testServiceProvider, BeaconState state, BeaconBlockHeader header, byte[] privateKey)
        {
            var signatureDomains = testServiceProvider.GetService<IOptions<SignatureDomains>>().Value;
            var beaconStateAccessor = testServiceProvider.GetService<BeaconStateAccessor>();

            var domain = beaconStateAccessor.GetDomain(state, signatureDomains.BeaconProposer, Epoch.None);
            var signingRoot = header.SigningRoot();
            var signature = TestSecurity.BlsSign(signingRoot, privateKey, domain);
            header.SetSignature(signature);
        }
    }
}
