// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Optimism.CL;
using Nethermind.Serialization.Json;
using NUnit.Framework;

namespace Nethermind.Optimism.Test.CL;

public class CLChainSpecEngineParametersTests
{
    [Test]
    public void Chain_spec_omitting_system_transaction_addresses_keeps_the_defaults()
    {
        CLChainSpecEngineParameters parameters = new EthereumJsonSerializer().Deserialize<CLChainSpecEngineParameters>("""{"l1ChainId":1}""")!;

        // Derivation builds the L1 info deposit from these, so losing them breaks every derived block.
        using (Assert.EnterMultipleScope())
        {
            Assert.That(parameters.SystemTransactionSender, Is.EqualTo(new Address("0xDeaDDEaDDeAdDeAdDEAdDEaddeAddEAdDEAd0001")));
            Assert.That(parameters.SystemTransactionTo, Is.EqualTo(new Address("0x4200000000000000000000000000000000000015")));
        }
    }
}
