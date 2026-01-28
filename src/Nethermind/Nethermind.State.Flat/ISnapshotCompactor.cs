// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Collections;

namespace Nethermind.State.Flat;

public interface ISnapshotCompactor
{
    bool DoCompactSnapshot(in StateId stateId);
    SnapshotPooledList GetSnapshotsToCompact(Snapshot snapshot);
    Snapshot CompactSnapshotBundle(SnapshotPooledList snapshots);
}
