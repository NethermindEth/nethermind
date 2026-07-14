// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Consensus.AuRa.Config;
using Nethermind.Core;
using Nethermind.Specs;
using NUnit.Framework;

namespace Nethermind.AuRa.Test
{
    public class AuRaPluginTests
    {
        [Test]
        public void ApplyToReleaseSpec_sets_Eip158IgnoredAccount()
        {
            AuRaChainSpecEngineParameters parameters = new();
            ReleaseSpec spec = new();

            parameters.ApplyToReleaseSpec(spec, 0, null);

            Assert.That(spec.Eip158IgnoredAccount, Is.EqualTo(Address.SystemUser));
        }

    }
}
