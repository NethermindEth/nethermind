// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

// PersistedSnapshotScanner<> is sealed (it sits on the persisted-snapshot read path), so the whole-read
// instantiation is named by an alias rather than a subclass. A using alias needs fully qualified names.
global using WholeReadScanner = Nethermind.State.Flat.PersistedSnapshots.PersistedSnapshotScanner<
    Nethermind.State.Flat.PersistedSnapshots.Storage.WholeReadSession,
    Nethermind.State.Flat.PersistedSnapshots.Storage.WholeReadSessionReader,
    Nethermind.State.Flat.Io.NoOpPin>;
