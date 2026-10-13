// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.State.Flat.Persistence;

namespace Nethermind.State.Flat.Rust.Fork;

/// <summary>
/// <see cref="Nethermind.State.Flat.FlatTrieVerifier"/> over the forked flat package: the same verification, reading
/// the trie through a forked <see cref="ReadOnlySnapshotBundle"/>.
/// </summary>
/// <remarks>
/// <see cref="Verify"/> repeats the original's public <c>Verify</c> with the fork's manager and adapter, then runs the
/// original's verification core, so the result and <see cref="Stats"/> are the original's.
/// </remarks>
/// <param name="scratchDirectory">Where preimage mode spools flat state while re-sorting it by hash.</param>
public sealed class FlatTrieVerifier(IFlatDbManager flatDbManager, IPersistence persistence, ILogManager logManager, string scratchDirectory)
{
    private readonly Nethermind.State.Flat.FlatTrieVerifier _originalVerifier = new(null!, persistence, logManager, scratchDirectory);
    private readonly ILogger _logger = logManager.GetClassLogger<Nethermind.State.Flat.FlatTrieVerifier>();

    public Nethermind.State.Flat.FlatTrieVerifier.VerificationStats Stats => _originalVerifier.Stats;

    public bool Verify(BlockHeader stateAtBlock, CancellationToken cancellationToken)
    {
        StateId stateId = new(stateAtBlock);
        using IPersistence.IPersistenceReader reader = persistence.CreateReader();
        if (reader.CurrentState != stateId)
        {
            _logger.Warn($"With flat, only the persisted state can be verified. Will use current persisted state: {reader.CurrentState}");
            stateId = reader.CurrentState;
        }

        // FullScan on the bundle only: trie nodes are read through its persistence reader. `reader` just
        // serves metadata and range iterators, which never take the readahead path, so it stays flagless
        // on the shared cached reader. The run therefore pins two snapshots: that one and the bundle's.
        using ReadOnlySnapshotBundle bundle = flatDbManager.GatherReadOnlySnapshotBundle(stateId, ReaderFlags.FullScan);
        ReadOnlyStateTrieStoreAdapter trieStore = new(bundle);

        return ForkInternals.VerifyCore(_originalVerifier, reader, trieStore, stateId.StateRoot.ToCommitment(), cancellationToken);
    }
}
