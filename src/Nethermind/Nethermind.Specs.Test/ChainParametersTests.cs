// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using NUnit.Framework;

namespace Nethermind.Specs.Test;

public class ChainParametersTests
{
    [Test]
    public void ChainParameters_should_have_same_properties_as_chainSpecParamsJson()
    {
        string[] chainParametersExceptions = {
            "Registrar"
        };
        string[] chainSpecParamsJsonExceptions = {
            "ChainId", "EnsRegistrar", "NetworkId"
        };
        IEnumerable<string> chainParametersProperties = typeof(ChainParameters).GetProperties()
            .Where(x => !chainParametersExceptions.Contains(x.Name))
            .Select(x => x.Name);
        IEnumerable<string> chainSpecParamsJsonProperties = typeof(ChainSpecParamsJson).GetProperties()
            .Where(x => !chainSpecParamsJsonExceptions.Contains(x.Name)).
            Select(x => x.Name);

        Assert.That(chainParametersProperties, Is.EquivalentTo(chainSpecParamsJsonProperties));
    }
}
