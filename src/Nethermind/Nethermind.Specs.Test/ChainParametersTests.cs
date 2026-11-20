// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Exceptions;
using Nethermind.Core.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using NUnit.Framework;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Nethermind.Specs.Test;

public class ChainParametersTests
{
    [Test]
    public void ChainParameters_should_have_same_properties_as_chainSpecParamsJson()
    {
        string[] chainParametersExceptions = [];
        string[] chainSpecParamsJsonExceptions = ["ChainId", "NetworkId", "NamedForks"];
        IEnumerable<string> chainParametersProperties = typeof(ChainParameters).GetProperties()
            .Where(x => !chainParametersExceptions.Contains(x.Name))
            .Select(x => x.Name);
        IEnumerable<string> chainSpecParamsJsonProperties = typeof(ChainSpecParamsJson).GetProperties()
            .Where(x => !chainSpecParamsJsonExceptions.Contains(x.Name)).
            Select(x => x.Name);

        Assert.That(chainParametersProperties, Is.EquivalentTo(chainSpecParamsJsonProperties));
    }

    [Test]
    public void ChainParameters_should_be_loaded_from_chainSpecParamsJson()
    {
        // eips with additional dependencies or non standard names
        string[] exceptions = [
            "MaxCodeSizeTransitionTimestamp",
            "Eip4844FeeCollectorTransitionTimestamp",
            "Eip6110TransitionTimestamp",
            "Eip8272TransitionTimestamp",
        ];

        const ulong testValue = 1ul;

        foreach (PropertyInfo jsonParamsProp in typeof(ChainSpecParamsJson).GetProperties().Where(x => x.Name.EndsWith("TransitionTimestamp") && !exceptions.Contains(x.Name)))
        {
            ChainSpecJson test = new() { Params = new ChainSpecParamsJson() };
            jsonParamsProp.SetValue(test.Params, testValue);
            (ChainSpecBasedSpecProvider? prov, ChainSpec? spec) = TestSpecHelper.LoadChainSpec(test);

            PropertyInfo? paramsProp = typeof(ChainParameters).GetProperty(jsonParamsProp.Name);

            Assert.That(paramsProp, Is.Not.Null, $"Property {jsonParamsProp.Name} not found in ChainParameters.");
            object? paramsValue = paramsProp.GetValue(spec.Parameters);
            Assert.That(paramsValue, Is.EqualTo(testValue), $"Property {jsonParamsProp.Name} in ChainParameters does not match the value set in ChainSpecParamsJson, got {paramsValue} expected {testValue}.");


            IReleaseSpec preSpec = prov.GetSpec(ForkActivation.TimestampOnly(0));
            string specPropName = $"Is{jsonParamsProp.Name.Replace("TransitionTimestamp", "")}Enabled";
            PropertyInfo? specProp = preSpec.GetType().GetProperty(specPropName);
            Assert.That(specProp, Is.Not.Null, $"Property {specPropName} not found in {preSpec.GetType().Name}.");

            Assert.That(specProp.GetValue(preSpec), Is.False, $"Property {specPropName} is activated, which was not expected.");

            IReleaseSpec postSpec = prov.GetSpec(ForkActivation.TimestampOnly(1));
            Assert.That(specProp.GetValue(postSpec), Is.True, $"Property {specPropName} is not activated.");
        }
    }

    [TestCase(null, true)]
    [TestCase(5UL, false)]
    [TestCase(10UL, false)]
    [TestCase(11UL, true)]
    public void Eip8272_refuses_to_activate_before_eip7843(ulong? slotNumberTransition, bool refused)
    {
        ChainSpecJson test = new()
        {
            Params = new ChainSpecParamsJson
            {
                Eip8141TransitionTimestamp = 0,
                Eip7843TransitionTimestamp = slotNumberTransition,
                Eip8272TransitionTimestamp = 10
            }
        };
        if (refused)
        {
            Assert.That(() => TestSpecHelper.LoadChainSpec(test),
                Throws.Exception.With.InnerException.InstanceOf<InvalidConfigurationException>());
            return;
        }
        (ChainSpecBasedSpecProvider provider, _) = TestSpecHelper.LoadChainSpec(test);
        Assert.That(provider.GetSpec(ForkActivation.TimestampOnly(10)).IsEip8272Enabled, Is.True);
    }
}
