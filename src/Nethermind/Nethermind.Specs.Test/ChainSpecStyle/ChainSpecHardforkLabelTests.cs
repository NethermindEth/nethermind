// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Nethermind.Core;
using Nethermind.Core.Exceptions;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
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

    /// <remarks>
    /// Label keys in <see cref="HardforkLabels.All"/> intentionally use the fork's class name
    /// (e.g. <c>ConstantinopleFix</c>), so we resolve each label to its <see cref="NamedReleaseSpec"/>
    /// by type-name lookup in the <see cref="Nethermind.Specs.Forks"/> namespace and reading the
    /// static <c>Instance</c> property. No hand-maintained label→fork dictionary needed.
    /// </remarks>
    private static NamedReleaseSpec ForkFor(IHardforkLabel label) =>
        // FlattenHierarchy is required: Instance is declared on NamedReleaseSpec<TSelf>, the
        // generic base, and reflection treats statics as belonging to the declaring type.
        typeof(NamedReleaseSpec).Assembly.GetType($"Nethermind.Specs.Forks.{label.LabelName}")
            ?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            ?.GetValue(null) as NamedReleaseSpec
        ?? throw new InvalidOperationException(
            $"No public Nethermind.Specs.Forks.{label.LabelName}.Instance — fork class is missing or renamed.");

    private static IEnumerable<TestCaseData> AllLabels() =>
        HardforkLabels.All.Select(l => new TestCaseData(l).SetArgDisplayNames(l.LabelName));

    private static IEnumerable<TestCaseData> LabelForkPairs() =>
        HardforkLabels.All.Select(l => new TestCaseData(l, ForkFor(l)).SetArgDisplayNames(l.LabelName));

    private static IEnumerable<TestCaseData> PostMergeLabels() =>
        HardforkLabels.All
            .Select(l => (Label: l, Fork: ForkFor(l)))
            .Where(p => p.Fork.IsPostMerge)
            .Select(p => new TestCaseData(p.Label, p.Fork).SetArgDisplayNames(p.Label.LabelName));

    [TestCaseSource(nameof(AllLabels))]
    public void Label_expands_to_all_constituent_transition_fields(IHardforkLabel label)
    {
        ChainSpec spec = Load($"\"{ToCamelCase(label.LabelName)}\": \"0x{ActivationValue:x}\"");

        foreach (string propName in label.EipPropertyNames)
        {
            object? actual = typeof(ChainParameters).GetProperty(propName)!.GetValue(spec.Parameters);
            Assert.That(Convert.ToUInt64(actual), Is.EqualTo(ActivationValue), $"{propName} should match the {label.LabelName} label");
        }
    }

    /// <remarks>
    /// Pins every <see cref="IHardforkLabel.Eips"/> entry against the
    /// <see cref="NamedReleaseSpec"/> for its fork: each canonical EIP must be enabled (or, for
    /// <c>Eip…DisableTransition</c> labels, disabled) on the named spec. Catches drift when a new
    /// EIP joins a fork class but not <see cref="HardforkLabels"/>.
    /// </remarks>
    [TestCaseSource(nameof(LabelForkPairs))]
    public void Label_eips_align_with_named_fork(IHardforkLabel label, NamedReleaseSpec fork)
    {
        bool isDisableLabel = label.EipPropertyNames.All(p => p.Contains("Disable", StringComparison.Ordinal));

        foreach (int eip in label.Eips)
        {
            string releaseProperty = $"IsEip{eip}Enabled";
            PropertyInfo? prop = typeof(ReleaseSpec).GetProperty(releaseProperty);
            Assert.That(prop, Is.Not.Null, $"ReleaseSpec should expose {releaseProperty} for label '{label.LabelName}'");
            using (Assert.EnterMultipleScope())
            {
                Assert.That((bool)prop!.GetValue(fork)!, Is.EqualTo(!isDisableLabel),
                    $"label '{label.LabelName}' covers EIP-{eip}, but {fork.Name}.{releaseProperty} is {prop.GetValue(fork)}");
            }
        }
    }

    /// <remarks>
    /// Reverse direction (post-merge only — pre-merge forks have too many irregular JSON names to
    /// pattern-match reliably): every EIP newly enabled by <paramref name="fork"/> over its parent
    /// that has a regular <c>Eip&lt;N&gt;TransitionTimestamp</c> field must be referenced by the
    /// label. Catches drift when a new timestamp-EIP joins a fork class but the label forgets to
    /// expand it.
    /// </remarks>
    [TestCaseSource(nameof(PostMergeLabels))]
    public void Post_merge_label_covers_every_eip_introduced_by_named_fork(IHardforkLabel label, NamedReleaseSpec fork)
    {
        NamedReleaseSpec parent = fork.Parent ?? throw new InvalidOperationException($"{fork.Name} has no parent");
        HashSet<int> labelEips = [.. label.Eips];

        foreach (PropertyInfo isEipEnabled in typeof(ReleaseSpec).GetProperties())
        {
            Match match = IsEipEnabledPattern.Match(isEipEnabled.Name);
            if (!match.Success) continue;
            if ((bool)isEipEnabled.GetValue(fork)! == (bool)isEipEnabled.GetValue(parent)!) continue;
            if (!(bool)isEipEnabled.GetValue(fork)!) continue;     // only newly enabled

            int eip = int.Parse(match.Groups["eip"].Value);
            // Skip EIPs that don't have a regular Eip{N}TransitionTimestamp counterpart on the JSON side.
            if (typeof(ChainSpecParamsJson).GetProperty($"Eip{eip}TransitionTimestamp") is null) continue;

            Assert.That(labelEips, Does.Contain(eip),
                $"{fork.Name} introduces EIP-{eip} over {parent.Name} — label '{label.LabelName}' should expand it");
        }
    }

    [Test]
    public void Label_and_redundant_explicit_eip_with_matching_value_is_accepted()
    {
        ChainSpec spec = Load("\"cancun\": \"0x100\", \"eip4844TransitionTimestamp\": \"0x100\"");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.Parameters.Eip4844TransitionTimestamp, Is.EqualTo(0x100));
            Assert.That(spec.Parameters.Eip1153TransitionTimestamp, Is.EqualTo(0x100));
        }
    }

    // NamedForks is initialized with StringComparer.OrdinalIgnoreCase — these all resolve to the same label.
    [TestCase("\"cancun\": \"0x100\"")]
    [TestCase("\"Cancun\": \"0x100\"")]
    [TestCase("\"CANCUN\": \"0x100\"")]
    public void Label_key_match_is_case_insensitive(string paramsJson)
    {
        ChainSpec spec = Load(paramsJson);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.Parameters.Eip4844TransitionTimestamp, Is.EqualTo(0x100));
            Assert.That(spec.Parameters.Eip1153TransitionTimestamp, Is.EqualTo(0x100));
        }
    }

    [Test]
    public void Label_conflicting_with_explicit_eip_throws()
    {
        // ChainSpecLoader.Load wraps the inner config exception in InvalidDataException.
        InvalidDataException exception = Assert.Throws<InvalidDataException>(
            () => Load("\"cancun\": \"0x100\", \"eip4844TransitionTimestamp\": \"0x200\""))!;
        Assert.That(exception.InnerException, Is.TypeOf<InvalidConfigurationException>());
        Assert.That(exception.InnerException!.Message, Does.Contain("Cancun").And.Contain("Eip4844TransitionTimestamp"));
    }

    [Test]
    public void Post_merge_labels_at_genesis_populate_header_roots()
    {
        ChainSpec spec = Load("\"shanghai\": \"0x0\", \"cancun\": \"0x0\", \"prague\": \"0x0\"");
        Block genesis = spec.Genesis ?? throw new AssertionException("Genesis was not loaded.");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(genesis.Header.WithdrawalsRoot, Is.Not.Null);
            Assert.That(genesis.Header.ParentBeaconBlockRoot, Is.Not.Null);
            Assert.That(genesis.Header.RequestsHash, Is.Not.Null);
        }
    }

    [Test]
    public void Per_eip_only_chainspec_is_unaffected()
    {
        ChainSpec spec = Load("\"eip4844TransitionTimestamp\": \"0x55\", \"eip4788TransitionTimestamp\": \"0x55\"");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(spec.Parameters.Eip4844TransitionTimestamp, Is.EqualTo(0x55));
            Assert.That(spec.Parameters.Eip4788TransitionTimestamp, Is.EqualTo(0x55));
            // Sibling EIPs not part of this declaration stay null.
            Assert.That(spec.Parameters.Eip1153TransitionTimestamp, Is.Null);
        }
    }

    private static string ToCamelCase(string name) =>
        char.ToLowerInvariant(name[0]) + name[1..];

    private static readonly Regex IsEipEnabledPattern =
        new(@"^IsEip(?<eip>\d+)Enabled$", RegexOptions.Compiled);
}
