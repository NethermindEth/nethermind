// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Specs.Forks;

namespace Nethermind.Specs.GnosisForks;

/// <summary>
/// Base class for Gnosis chain forks. Each Gnosis fork takes two references:
/// <list type="bullet">
///   <item><c>mainnetFork</c> — the mainnet fork whose EIP delta this Gnosis fork incorporates
///     (e.g. <see cref="Cancun"/> for <c>CancunGnosis</c>).</item>
///   <item><c>gnosisParent</c> — the previous Gnosis fork in the chain, or <c>null</c> for the
///     first Gnosis fork (<c>LondonGnosis</c>), in which case the parent defaults to
///     <c>mainnetFork.Parent</c> (i.e. the mainnet fork just before the divergence point).</item>
/// </list>
///
/// <para>
/// <see cref="Apply"/> calls <c>mainnetFork.Apply(spec)</c> to include the mainnet delta.
/// Concrete Gnosis forks override <see cref="Apply"/>, call <c>base.Apply(spec)</c> first,
/// then add Gnosis-specific overrides. For example, <c>CancunGnosis</c>:
/// <code>
///   Olympic → … → Berlin → London.Apply → LondonGnosis.Apply(base + FeeCollector)
///     → ShanghaiGnosis.Apply(base=Shanghai) → CancunGnosis.Apply(base=Cancun + blob overrides)
/// </code>
/// </para>
/// </summary>
public abstract class NamedGnosisReleaseSpec(NamedReleaseSpec mainnetFork, NamedReleaseSpec? gnosisParent = null)
    : NamedReleaseSpec(gnosisParent ?? mainnetFork.Parent)
{
    /// <summary>
    /// Applies the corresponding mainnet fork's delta. Concrete Gnosis forks should call
    /// <c>base.Apply(spec)</c> first, then set Gnosis-specific overrides.
    /// </summary>
    public override void Apply(NamedReleaseSpec spec) => mainnetFork.Apply(spec);
}

/// <summary>
/// Generic base that adds a per-fork-type singleton <see cref="Instance"/>.
/// Concrete Gnosis forks inherit as:
/// <c>class CancunGnosis() : NamedGnosisReleaseSpec&lt;CancunGnosis&gt;(Cancun.Instance, ShanghaiGnosis.Instance)</c>.
/// </summary>
public abstract class NamedGnosisReleaseSpec<TSelf>(NamedReleaseSpec mainnetFork, NamedReleaseSpec? gnosisParent = null)
    : NamedGnosisReleaseSpec(mainnetFork, gnosisParent)
    where TSelf : NamedGnosisReleaseSpec<TSelf>, new()
{
    public static NamedReleaseSpec Instance { get; } = new TSelf();
}
