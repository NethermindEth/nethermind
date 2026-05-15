// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text.RegularExpressions;
using Nethermind.Config;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.Specs.ChainSpecStyle;

/// <summary>
/// Shorthand hardfork labels for the Parity-style chainspec JSON. Each label is a single
/// activation value — a block number for pre-Shanghai forks (<c>long?</c>) and a timestamp for
/// post-merge forks (<c>ulong?</c>) — that fans out at load time to the full set of per-EIP
/// transition fields that make up that fork.
/// </summary>
/// <remarks>
/// Resolution happens once at the JSON parse boundary so downstream code keeps reading the
/// individual per-EIP fields and stays unaware of the shorthand. A label and an explicit per-EIP
/// field may coexist with matching values (redundant); conflicting values are rejected at load.
/// <para>
/// Adding a new EIP to an existing fork is one new line — the property-access expression doubles
/// as the source for both the name (used in diagnostics) and the get/set delegates (via
/// <see cref="ExpressionExtensions"/>).
/// </para>
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
    /// Expands every set hardfork label on <paramref name="parameters"/> into its constituent
    /// per-EIP transition fields. Throws <see cref="InvalidConfigurationException"/> when a label
    /// disagrees with an explicit per-EIP value.
    /// </summary>
    public static void ExpandAll(ChainSpecParamsJson parameters)
    {
        foreach (IHardforkLabel label in All) label.Apply(parameters);
    }

    internal static HardforkLabel<long> Block(
        Expression<Func<ChainSpecParamsJson, long?>> label,
        params Expression<Func<ChainSpecParamsJson, long?>>[] eips) => new(label, eips);

    internal static HardforkLabel<ulong> Time(
        Expression<Func<ChainSpecParamsJson, ulong?>> label,
        params Expression<Func<ChainSpecParamsJson, ulong?>>[] eips) => new(label, eips);
}

public interface IHardforkLabel
{
    /// <summary>JSON key of the label, e.g. <c>Shanghai</c>.</summary>
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
    /// Copies the label value (if set) into each constituent EIP field that is still unset.
    /// Throws <see cref="InvalidConfigurationException"/> when an EIP field is already set to a
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

    private readonly Func<ChainSpecParamsJson, T?> _readLabel;
    private readonly EipAccessor[] _eips;

    public string LabelName { get; }
    public IReadOnlyList<string> EipPropertyNames { get; }
    public IReadOnlyList<int> Eips { get; }

    public HardforkLabel(
        Expression<Func<ChainSpecParamsJson, T?>> label,
        Expression<Func<ChainSpecParamsJson, T?>>[] eips)
    {
        LabelName = label.GetName();
        _readLabel = label.Compile();
        _eips = [.. eips.Select(static e => new EipAccessor(e.GetName(), e.Compile(), e.GetSetter()))];
        EipPropertyNames = [.. _eips.Select(static e => e.Name)];
        Eips = [.. EipPropertyNames.Select(ParseCanonicalEip).Distinct()];
    }

    public void Apply(ChainSpecParamsJson parameters)
    {
        if (_readLabel(parameters) is not { } labelValue) return;

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
                    $"Chainspec hardfork label '{LabelName}' = 0x{labelValue.ToString("x", null)} conflicts with explicit {eip.Name} = 0x{current.Value.ToString("x", null)}. Either remove the conflicting field or align both values.",
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

    private readonly record struct EipAccessor(string Name, Func<ChainSpecParamsJson, T?> Read, Action<ChainSpecParamsJson, T?> Write);
}
