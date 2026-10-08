// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.State.Flat;
using Nethermind.State.Flat.Persistence.BloomFilter;
using Nethermind.State.Pbt.PersistedSnapshots;

namespace Nethermind.State.Pbt;

public partial class PbtSnapshotRepository
{
    internal bool ContainsRetainedStorageSource(PbtRetainedSnapshot snapshot)
    {
        lock (_lock)
            return _retained.TryGetValue((snapshot.To, unchecked((long)(snapshot.To.BlockNumber - snapshot.From.BlockNumber))), out PbtRetainedSnapshot? current)
                && current.From == snapshot.From && current.To == snapshot.To && current.TreeRoot == snapshot.TreeRoot
                && current.Tier == snapshot.Tier && current.Location == snapshot.Location;
    }

    internal bool ReplaceRetainedSnapshot(PbtRetainedSnapshot expected, PbtRetainedSnapshot replacement)
    {
        lock (_publicationGate.Sync)
        {
            lock (_lock)
            {
                (StateId, long) key = (expected.To, unchecked((long)(expected.To.BlockNumber - expected.From.BlockNumber)));
                if (!_retained.TryGetValue(key, out PbtRetainedSnapshot? current) || !ReferenceEquals(current, expected)) return false;
                if (replacement.From != expected.From || replacement.To != expected.To || replacement.TreeRoot != expected.TreeRoot
                    || replacement.Tier != expected.Tier || replacement.Location != expected.Location)
                    throw new InvalidOperationException("Bloom replacement must preserve the retained snapshot's identity and storage.");
                if (!replacement.TryLease()) return false;
                _retained[key] = replacement;
                _retainedEdges[(expected.To, expected.Tier)][key.Item2] = replacement;
            }
            expected.Dispose();
            return true;
        }
    }

    internal void ShareBloomAcrossRange(StateId from, StateId to, RefCountedBloomFilter sharedBloom)
    {
        lock (_publicationGate.Sync)
        {
            List<PbtRetainedSnapshot> candidates = [];
            try
            {
                lock (_lock)
                {
                    HashSet<StateId> ancestry = [to];
                    List<StateId> states = [];
                    StateId current = to;
                    while (Height(current) > Height(from))
                    {
                        if (!_retainedEdges.TryGetValue((current, SnapshotTier.PersistedBase), out SortedDictionary<long, PbtRetainedSnapshot>? bases)
                            || bases.Count == 0) break;
                        PbtRetainedSnapshot? baseSnapshot = null;
                        foreach (PbtRetainedSnapshot snapshot in bases.Values) { baseSnapshot = snapshot; break; }
                        states.Add(current);
                        if (!ancestry.Add(baseSnapshot!.From)) break;
                        current = baseSnapshot.From;
                    }
                    foreach (StateId state in states)
                        foreach (SnapshotTier tier in RetainedPriority)
                            if (_retainedEdges.TryGetValue((state, tier), out SortedDictionary<long, PbtRetainedSnapshot>? edges))
                                foreach (PbtRetainedSnapshot snapshot in edges.Values)
                                    if (!ReferenceEquals(snapshot.BloomRef, sharedBloom) && Height(snapshot.From) >= Height(from)
                                        && ancestry.Contains(snapshot.From) && snapshot.TryLease()) candidates.Add(snapshot);
                }

                // A clear may remove a descriptor from the widest diff that an earlier retained
                // state still needs. Populate the historical superset before rebinding any reader.
                foreach (PbtRetainedSnapshot snapshot in candidates)
                {
                    using PbtRetainedScanner scanner = snapshot.Scan();
                    while (scanner.MoveNext())
                        if (scanner.Key[0] is not 0 and not PbtRetainedKey.Ownership && !PbtRetainedSnapshot.IsChunk(scanner.Key))
                        {
                            ulong key = PbtRetainedKey.BloomHash(scanner.Key);
                            if (!sharedBloom.Filter.MightContain(key)) sharedBloom.Filter.Add(key);
                        }
                }
                foreach (PbtRetainedSnapshot snapshot in candidates)
                {
                    using PbtRetainedSnapshot replacement = snapshot.WithBloom(sharedBloom);
                    ReplaceRetainedSnapshot(snapshot, replacement);
                }
            }
            finally
            {
                foreach (PbtRetainedSnapshot snapshot in candidates) snapshot.Dispose();
            }
        }
    }
}
