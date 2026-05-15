// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Specs;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Specs.Test.ChainSpecStyle;

[Parallelizable(ParallelScope.All)]
public class ChainSpecHardforkLabelTests
{
    // Same activation value for every label — non-zero so we never confuse "label not set" with
    // "label set to genesis"; hex so it round-trips identically through chainspec JSON.
    private const ulong ActivationValue = 0x65687fd0;

    // chainID=1 lets the EIP-6110 deposit-contract fallback resolve when a Prague-bearing label is expanded.
    private static ChainSpec Load(string paramsJson)
    {
        string json = $$"""
            {
              "name": "Test",
              "engine": { "NethDev": {} },
              "params": {
                "networkID": "0x1",
                "chainID": "0x1",
                {{paramsJson}}
              },
              "genesis": { "seal": { "ethereum": { "nonce": "0x0", "mixHash": "0x0000000000000000000000000000000000000000000000000000000000000000" } }, "difficulty": "0x1", "author": "0x0000000000000000000000000000000000000000", "timestamp": "0x0", "parentHash": "0x0000000000000000000000000000000000000000000000000000000000000000", "extraData": "0x", "gasLimit": "0x1388" },
              "accounts": {}
            }
            """;
        ChainSpecLoader loader = new(new EthereumJsonSerializer(), LimboLogs.Instance);
        return loader.Load(new MemoryStream(Encoding.UTF8.GetBytes(json)));
    }

    private static IEnumerable<TestCaseData> AllLabels() =>
        HardforkLabels.All.Select(l => new TestCaseData(l).SetArgDisplayNames(l.LabelName));

    [TestCaseSource(nameof(AllLabels))]
    public void Label_expands_to_all_constituent_transition_fields(IHardforkLabel label)
    {
        ChainSpec spec = Load($"\"{ToCamelCase(label.LabelName)}\": \"0x{ActivationValue:x}\"");

        foreach (string propName in label.EipPropertyNames)
        {
            object? actual = typeof(ChainParameters).GetProperty(propName)!.GetValue(spec.Parameters);
            Convert.ToUInt64(actual).Should().Be(ActivationValue, $"{propName} should match the {label.LabelName} label");
        }
    }

    private static IEnumerable<TestCaseData> LabelForkPairs() => new TestCaseData[]
    {
        new("Homestead", Homestead.Instance),
        new("TangerineWhistle", TangerineWhistle.Instance),
        new("SpuriousDragon", SpuriousDragon.Instance),
        new("Byzantium", Byzantium.Instance),
        new("Constantinople", Constantinople.Instance),
        new("ConstantinopleFix", ConstantinopleFix.Instance),
        new("Istanbul", Istanbul.Instance),
        new("Berlin", Berlin.Instance),
        new("London", London.Instance),
        new("Shanghai", Shanghai.Instance),
        new("Cancun", Cancun.Instance),
        new("Prague", Prague.Instance),
        new("Osaka", Osaka.Instance),
        new("Amsterdam", Amsterdam.Instance),
    }.Select(d => d.SetArgDisplayNames((string)d.Arguments[0]!));

    /// <remarks>
    /// Pins every <see cref="IHardforkLabel.EipPropertyNames"/> entry against the
    /// <see cref="NamedReleaseSpec"/> for its fork: the EIP number parsed from the JSON property
    /// must be enabled (or, for <c>Eip…DisableTransition</c>, disabled) on the named spec. Catches
    /// drift when a new EIP is added to a fork class but not to <see cref="HardforkLabels"/>.
    /// </remarks>
    [TestCaseSource(nameof(LabelForkPairs))]
    public void Label_eip_properties_align_with_named_fork(string labelName, NamedReleaseSpec fork)
    {
        IHardforkLabel label = HardforkLabels.All.Single(l => l.LabelName == labelName);

        foreach (string prop in label.EipPropertyNames)
        {
            Match match = EipPropertyPattern.Match(prop);
            match.Success.Should().BeTrue($"label property {prop} should follow the Eip<N>[Disable]Transition[Timestamp] convention");

            int eipFromName = int.Parse(match.Groups["eip"].Value);
            bool isParitySplit = match.Groups["split"].Success;       // Eip161abc / Eip161d → EIP-158
            bool isDisable = match.Groups["disable"].Success;

            int releaseSpecEip = isParitySplit ? 158 : eipFromName;
            string releaseProperty = $"IsEip{releaseSpecEip}Enabled";

            bool? enabled = (bool?)typeof(ReleaseSpec).GetProperty(releaseProperty)?.GetValue(fork);
            enabled.Should().NotBeNull($"IReleaseSpec should expose {releaseProperty} for label '{labelName}'");
            enabled!.Value.Should().Be(!isDisable,
                $"label '{labelName}' maps {prop} → EIP-{releaseSpecEip}, but {fork.Name} has {releaseProperty}={enabled}");
        }
    }

    /// <remarks>
    /// Reverse direction (post-merge only — pre-merge forks have too many irregular JSON names to
    /// pattern-match reliably): every EIP newly enabled in <paramref name="fork"/> over its
    /// <paramref name="parent"/> that has a regular <c>Eip&lt;N&gt;TransitionTimestamp</c> field
    /// must be referenced by the label. Catches drift when a new timestamp-EIP joins a fork
    /// class but the label forgets to expand it.
    /// </remarks>
    private static IEnumerable<TestCaseData> PostMergeForkPairs() => new TestCaseData[]
    {
        new("Shanghai", Shanghai.Instance, Paris.Instance),
        new("Cancun", Cancun.Instance, Shanghai.Instance),
        new("Prague", Prague.Instance, Cancun.Instance),
        new("Osaka", Osaka.Instance, Prague.Instance),
        new("Amsterdam", Amsterdam.Instance, BPO5.Instance),
    }.Select(d => d.SetArgDisplayNames((string)d.Arguments[0]!));

    [TestCaseSource(nameof(PostMergeForkPairs))]
    public void Post_merge_label_covers_every_eip_introduced_by_named_fork(string labelName, NamedReleaseSpec fork, NamedReleaseSpec parent)
    {
        IHardforkLabel label = HardforkLabels.All.Single(l => l.LabelName == labelName);
        HashSet<string> labelProperties = [.. label.EipPropertyNames];

        foreach (System.Reflection.PropertyInfo isEipEnabled in typeof(ReleaseSpec).GetProperties())
        {
            Match match = IsEipEnabledPattern.Match(isEipEnabled.Name);
            if (!match.Success) continue;

            bool inFork = (bool)isEipEnabled.GetValue(fork)!;
            bool inParent = (bool)isEipEnabled.GetValue(parent)!;
            if (!inFork || inParent) continue;

            int eip = int.Parse(match.Groups["eip"].Value);
            string expectedJsonField = $"Eip{eip}TransitionTimestamp";
            if (typeof(ChainSpecParamsJson).GetProperty(expectedJsonField) is null) continue;

            labelProperties.Should().Contain(expectedJsonField,
                $"{fork.Name} introduces EIP-{eip} over {parent.Name} — label '{labelName}' should expand {expectedJsonField}");
        }
    }

    [Test]
    public void Label_and_redundant_explicit_eip_with_matching_value_is_accepted()
    {
        ChainSpec spec = Load("\"cancun\": \"0x100\", \"eip4844TransitionTimestamp\": \"0x100\"");

        spec.Parameters.Eip4844TransitionTimestamp.Should().Be(0x100);
        spec.Parameters.Eip1153TransitionTimestamp.Should().Be(0x100);
    }

    [Test]
    public void Label_conflicting_with_explicit_eip_throws()
    {
        Action act = () => Load("\"cancun\": \"0x100\", \"eip4844TransitionTimestamp\": \"0x200\"");

        // ChainSpecLoader.Load wraps the inner config exception in InvalidDataException.
        act.Should().Throw<InvalidDataException>()
            .WithInnerException<InvalidConfigurationException>()
            .WithMessage("*Cancun*Eip4844TransitionTimestamp*");
    }

    [Test]
    public void Per_eip_only_chainspec_is_unaffected()
    {
        ChainSpec spec = Load("\"eip4844TransitionTimestamp\": \"0x55\", \"eip4788TransitionTimestamp\": \"0x55\"");

        spec.Parameters.Eip4844TransitionTimestamp.Should().Be(0x55);
        spec.Parameters.Eip4788TransitionTimestamp.Should().Be(0x55);
        // Sibling EIPs not part of this declaration stay null.
        spec.Parameters.Eip1153TransitionTimestamp.Should().BeNull();
    }

    private static string ToCamelCase(string name) =>
        char.ToLowerInvariant(name[0]) + name[1..];

    private static readonly Regex EipPropertyPattern =
        new(@"^Eip(?<eip>\d+)(?<split>abc|d)?(?<disable>Disable)?Transition(Timestamp)?$", RegexOptions.Compiled);

    private static readonly Regex IsEipEnabledPattern =
        new(@"^IsEip(?<eip>\d+)Enabled$", RegexOptions.Compiled);
}
