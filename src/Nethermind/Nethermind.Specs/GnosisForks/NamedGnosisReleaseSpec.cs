// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Threading;
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
/// During construction, <see cref="NamedReleaseSpec.ReplayForkChain"/> walks from root to this fork.
/// At each Gnosis node, <see cref="ApplyDelta"/> first applies the mainnet fork's delta
/// (<c>mainnetFork.Apply</c>), then this fork's own <see cref="NamedReleaseSpec.Apply"/>.
/// This produces the correct combined chain, for example for <c>CancunGnosis</c>:
/// <code>
///   Olympic → … → Berlin → London.Apply → LondonGnosis.Apply(FeeCollector)
///     → Shanghai.Apply → ShanghaiGnosis.Apply → Cancun.Apply → CancunGnosis.Apply(blob overrides)
/// </code>
/// </para>
/// </summary>
public abstract class NamedGnosisReleaseSpec(NamedReleaseSpec mainnetFork, NamedReleaseSpec? gnosisParent = null)
    : NamedReleaseSpec(gnosisParent ?? mainnetFork.Parent)
{
    /// <summary>
    /// Applies the corresponding mainnet fork's delta first, then this Gnosis fork's own overrides.
    /// </summary>
    protected override void ApplyDelta(ReleaseSpec target)
    {
        mainnetFork.Apply(target);
        Apply(target);
    }
}

/// <summary>
/// Generic base that adds a per-fork-type lazy singleton <see cref="Instance"/>.
/// Concrete Gnosis forks inherit as:
/// <c>class CancunGnosis() : NamedGnosisReleaseSpec&lt;CancunGnosis&gt;(Cancun.Instance, ShanghaiGnosis.Instance)</c>.
/// </summary>
public abstract class NamedGnosisReleaseSpec<TSelf>(NamedReleaseSpec mainnetFork, NamedReleaseSpec? gnosisParent = null)
    : NamedGnosisReleaseSpec(mainnetFork, gnosisParent)
    where TSelf : NamedGnosisReleaseSpec<TSelf>, new()
{
    // ReSharper disable once StaticMemberInGenericType
    private static NamedReleaseSpec? _instance;

    public static NamedReleaseSpec Instance => LazyInitializer.EnsureInitialized(ref _instance, static () => new TSelf());
}
