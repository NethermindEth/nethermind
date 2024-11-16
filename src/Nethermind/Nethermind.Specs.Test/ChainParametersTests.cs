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

    [Test]
    public void SettingDencunTransitionTimestamp_SetsAllEipTimestamps()
    {
        var chainParameters = new ChainParameters();
        ulong timestamp = 0x65687fd0;

        // Set Dencun label timestamp
        chainParameters.DencunTransitionTimestamp = timestamp;

        // Verify all associated EIPs are set to the same timestamp
        Assert.That(chainParameters.Eip4844TransitionTimestamp, Is.EqualTo(timestamp));
        Assert.That(chainParameters.Eip4788TransitionTimestamp, Is.EqualTo(timestamp));
        Assert.That(chainParameters.Eip1153TransitionTimestamp, Is.EqualTo(timestamp));
        Assert.That(chainParameters.Eip5656TransitionTimestamp, Is.EqualTo(timestamp));
        Assert.That(chainParameters.Eip6780TransitionTimestamp, Is.EqualTo(timestamp));
    }

    [Test]
    public void GettingDencunTransitionTimestamp_ReturnsTimestampWhenAllMatch()
    {
        var chainParameters = new ChainParameters();
        ulong timestamp = 0x65687fd0;
        chainParameters.Eip4844TransitionTimestamp = timestamp;
        chainParameters.Eip4788TransitionTimestamp = timestamp;
        chainParameters.Eip1153TransitionTimestamp = timestamp;
        chainParameters.Eip5656TransitionTimestamp = timestamp;
        chainParameters.Eip6780TransitionTimestamp = timestamp;
        Assert.That(chainParameters.DencunTransitionTimestamp, Is.EqualTo(timestamp));
    }


    [Test]
    public void GettingDencunTransitionTimestamp_ReturnsNullWhenTimestampsDiffer()
    {
        var chainParameters = new ChainParameters();
        ulong timestamp = 0x65687fd0;
        chainParameters.Eip4844TransitionTimestamp = timestamp;
        chainParameters.Eip4788TransitionTimestamp = timestamp;
        chainParameters.Eip1153TransitionTimestamp = timestamp;
        chainParameters.Eip5656TransitionTimestamp = timestamp;
        chainParameters.Eip6780TransitionTimestamp = timestamp + 1; // Conflict is made here
        Assert.That(chainParameters.DencunTransitionTimestamp, Is.Null); // Test to see if Dencun label getter returns null due to conflict or not
    }

    [Test]
    public void ValidateNoTimestampConflicts_ReturnsTrueWhenAllMatch()
    {
        var chainParameters = new ChainParameters();
        ulong timestamp = 0x65687fd0;
        chainParameters.DencunTransitionTimestamp = timestamp;
        Assert.That(chainParameters.ValidateNoTimestampConflicts(), Is.True);
    }

}
