// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.BeaconChain.StateTransition;
using Nethermind.Core.Crypto;

namespace Nethermind.BeaconChain.ForkChoice;

/// <summary>An immutable copy of a head state's EIP-7917 <c>proposer_lookahead</c>, tagged with the block its shuffling was decided by.</summary>
/// <remarks>
/// fulu/beacon-chain.md <c>process_proposer_lookahead</c> fills the lookahead of a state at <see cref="Epoch"/> from the
/// state at the end of the previous epoch, so every slot it covers has the proposers of any chain whose latest block
/// before the first slot of <see cref="Epoch"/> is <see cref="DependentRoot"/>.
/// </remarks>
public sealed class ProposerLookaheadSnapshot
{
    private readonly ulong[] _proposers;

    /// <param name="epoch">The current epoch of the state the lookahead was read from.</param>
    /// <param name="dependentRoot">
    /// The latest block before the first slot of <paramref name="epoch"/> on the state's chain, or the fork-choice tree root
    /// when that block is below it: every chain fork choice holds shares the tree root's ancestry.
    /// </param>
    /// <param name="proposers">The state's <c>proposer_lookahead</c>; copied, since the state may be mutated afterwards.</param>
    public ProposerLookaheadSnapshot(ulong epoch, Hash256 dependentRoot, ReadOnlySpan<ulong> proposers)
    {
        Epoch = epoch;
        DependentRoot = dependentRoot;
        _proposers = proposers.ToArray();
    }

    public ulong Epoch { get; }

    public Hash256 DependentRoot { get; }

    /// <summary>The first slot whose proposer this lookahead holds.</summary>
    public ulong StartSlot => BeaconStateAccessors.ComputeStartSlotAtEpoch(Epoch);

    /// <summary>Finds the dependent root of a lookahead whose first slot is <paramref name="startSlot"/> for the chain through <paramref name="blockRoot"/>.</summary>
    /// <param name="nodes">Fork-choice nodes in proto-array order, parents before children.</param>
    /// <returns>
    /// The latest block before <paramref name="startSlot"/> on that chain; the deepest block of it in <paramref name="nodes"/> when that
    /// block is below them; or <c>null</c> when <paramref name="blockRoot"/> is not in <paramref name="nodes"/>.
    /// </returns>
    public static Hash256? FindDependentRoot(IReadOnlyList<ForkChoiceSnapshotNode> nodes, Hash256 blockRoot, ulong startSlot)
    {
        Hash256? wanted = blockRoot;
        Hash256? deepest = null;
        for (int i = nodes.Count - 1; i >= 0 && wanted is not null; i--)
        {
            ForkChoiceSnapshotNode node = nodes[i];
            if (node.Root != wanted)
            {
                continue;
            }

            if (node.Slot < startSlot)
            {
                return node.Root;
            }

            deepest = node.Root;
            wanted = node.ParentRoot;
        }

        return deepest;
    }

    /// <summary>Reads the proposer the lookahead names for <paramref name="slot"/>.</summary>
    /// <returns><c>false</c> for a slot outside the lookahead's window.</returns>
    public bool TryGetProposer(ulong slot, out ulong proposerIndex)
    {
        ulong offset = slot - StartSlot;
        if (slot < StartSlot || offset >= (ulong)_proposers.Length)
        {
            proposerIndex = 0;
            return false;
        }

        proposerIndex = _proposers[offset];
        return true;
    }
}

/// <summary>Hands the importer's latest <see cref="ProposerLookaheadSnapshot"/> to readers on other threads (column gossip).</summary>
/// <remarks><see cref="Current"/> stays <c>null</c> until the first head computation.</remarks>
public sealed class ProposerLookaheadHolder
{
    private ProposerLookaheadSnapshot? _current;

    public ProposerLookaheadSnapshot? Current
    {
        get => Volatile.Read(ref _current);
        set => Volatile.Write(ref _current, value);
    }
}
