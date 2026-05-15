// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Json;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// Shorthand hardfork labels for the Parity-style chainspec JSON. Each label is a single
/// activation value — a block number for pre-Shanghai forks (<c>long?</c>) and a timestamp for
/// post-merge forks (<c>ulong?</c>) — that fans out at load time to the full set of per-EIP
/// transition fields that make up that fork.
/// </summary>
/// <remarks>
/// Labels are captured during JSON deserialization by
/// <see cref="ChainSpecParamsJson.NamedForks"/>; <see cref="ExpandAll"/> consumes them after
/// parsing so downstream code keeps reading the individual per-EIP fields and stays unaware of
/// the shorthand. A label and an explicit per-EIP field may coexist with matching values
/// (redundant); conflicting values are rejected at load.
/// </remarks>
public static partial class HardforkLabels
{
    /// <summary>All labels in canonical fork order. Exposed for tests and tooling.</summary>
    /// <remarks>
    /// The contents are produced by <c>Nethermind.Analyzers.HardforkLabelsGenerator</c> from the
    /// <c>Nethermind.Specs.Forks.*</c> <see cref="Nethermind.Specs.Forks.NamedReleaseSpec"/>
    /// subclasses — Forks/*.cs is the single source of truth for the per-fork EIP set.
    /// </remarks>
    public static IReadOnlyList<IHardforkLabel> All { get; } = BuildAll();

    /// <summary>Implemented by the source generator; emits the explicit <c>Block</c>/<c>Time</c> registrations.</summary>
    private static partial IReadOnlyList<IHardforkLabel> BuildAll();

    /// <summary>
    /// Expands every hardfork label present in <see cref="ChainSpecParamsJson.NamedForks"/>
    /// into its constituent per-EIP transition fields, consuming the entries on success. Throws
    /// <see cref="InvalidConfigurationException"/> when a label disagrees with an explicit per-EIP
    /// value.
    /// </summary>
    public static void ExpandAll(ChainSpecParamsJson parameters)
    {
        if (parameters.NamedForks is null or { Count: 0 }) return;
        foreach (IHardforkLabel label in All) label.Apply(parameters);
    }

    /// <param name="name">Fork class name (e.g. <c>Cancun</c>); the JSON wire key is its camelCase form.</param>
    internal static HardforkLabel<long> Block(
        string name,
        params Expression<Func<ChainSpecParamsJson, long?>>[] eips) =>
        new(name, static e => e.Deserialize<long>(EthereumJsonSerializer.JsonOptions), eips);

    /// <inheritdoc cref="Block"/>
    internal static HardforkLabel<ulong> Time(
        string name,
        params Expression<Func<ChainSpecParamsJson, ulong?>>[] eips) =>
        new(name, static e => e.Deserialize<ulong>(EthereumJsonSerializer.JsonOptions), eips);
}

public interface IHardforkLabel
{
    /// <summary>Fork class name in PascalCase, e.g. <c>Shanghai</c>. The JSON wire key is its camelCase form.</summary>
    string LabelName { get; }

    /// <summary>Property names of every per-EIP transition field this label fans out to.</summary>
    IReadOnlyList<string> EipPropertyNames { get; }

    /// <summary>
    /// Canonical EIP numbers activated (or, for <c>Eip&lt;N&gt;DisableTransition</c>, revoked) by
    /// this label, parsed from <see cref="EipPropertyNames"/> and deduplicated.
    /// </summary>
    /// <remarks>
    /// Parity-split fields (<c>Eip161abcTransition</c>, <c>Eip161dTransition</c>) both fold to
    /// EIP-158 — the canonical EIP number that the runtime <c>IsEip158Enabled</c> flag tracks.
    /// </remarks>
    IReadOnlyList<int> Eips { get; }

    /// <summary>
    /// If <see cref="ChainSpecParamsJson.NamedForks"/> carries this label's key, copies the
    /// value into each constituent EIP field that is still unset and removes the entry. Throws
    /// <see cref="InvalidConfigurationException"/> when an EIP field is already set to a
    /// different value.
    /// </summary>
    void Apply(ChainSpecParamsJson parameters);
}

internal sealed class HardforkLabel<T> : IHardforkLabel
    where T : struct, IEquatable<T>, IFormattable
{
    // Eip{N}[abc|d][Disable]Transition[Timestamp] — captures the EIP number and the Parity-split
    // marker so EipPropertyNames can be normalized to canonical EIPs in `Eips`.
    private static readonly Regex EipPropertyPattern =
        new(@"^Eip(?<eip>\d+)(?<split>abc|d)?(?:Disable)?Transition(?:Timestamp)?$", RegexOptions.Compiled);

    private readonly Func<JsonElement, T> _parse;
    private readonly string _jsonKey;
    private readonly EipAccessor[] _eips;

    public string LabelName { get; }
    public IReadOnlyList<string> EipPropertyNames { get; }
    public IReadOnlyList<int> Eips { get; }

    public HardforkLabel(string labelName, Func<JsonElement, T> parse, Expression<Func<ChainSpecParamsJson, T?>>[] eips)
    {
        LabelName = labelName;
        _jsonKey = ToCamelCase(labelName);
        _parse = parse;
        _eips = [.. eips.Select(static e => new EipAccessor(e.GetName(), e.Compile(), e.GetSetter()))];
        EipPropertyNames = [.. _eips.Select(static e => e.Name)];
        Eips = [.. EipPropertyNames.Select(ParseCanonicalEip).Distinct()];
    }

    public void Apply(ChainSpecParamsJson parameters)
    {
        if (parameters.NamedForks is null) return;
        if (!parameters.NamedForks.Remove(_jsonKey, out JsonElement element)) return;

        T labelValue = _parse(element);

        foreach (EipAccessor eip in _eips)
        {
            T? current = eip.Read(parameters);
            if (current is null)
            {
                eip.Write(parameters, labelValue);
            }
            else if (!current.Value.Equals(labelValue))
            {
                throw new InvalidConfigurationException(
                    $"Chainspec hardfork label '{_jsonKey}' = 0x{labelValue.ToString("x", null)} conflicts with explicit {eip.Name} = 0x{current.Value.ToString("x", null)}. Either remove the conflicting field or align both values.",
                    ExitCodes.ConflictingChainspecEipConfiguration);
            }
        }
    }

    private static int ParseCanonicalEip(string propertyName)
    {
        Match m = EipPropertyPattern.Match(propertyName);
        if (!m.Success) throw new ArgumentException(
            $"Property name '{propertyName}' does not follow the Eip<N>[abc|d][Disable]Transition[Timestamp] convention.",
            nameof(propertyName));
        // Parity chainspecs split EIP-158 (state-clearing) into Eip161abc + Eip161d; both map to EIP-158.
        return m.Groups["split"].Success ? 158 : int.Parse(m.Groups["eip"].Value);
    }

    private static string ToCamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];

    private readonly record struct EipAccessor(string Name, Func<ChainSpecParamsJson, T?> Read, Action<ChainSpecParamsJson, T?> Write);
}
