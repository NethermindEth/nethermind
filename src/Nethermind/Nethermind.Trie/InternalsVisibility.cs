// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;

// Production friends - need access to TrieSyncNode (transient sync RLP wrapper).
// TrieSyncNode is internal to enforce the "sync code only" contract; widening this
// list should be reviewed carefully.
[assembly: InternalsVisibleTo("Nethermind.Synchronization")]
[assembly: InternalsVisibleTo("Nethermind.State.Flat")]
[assembly: InternalsVisibleTo("Nethermind.Benchmark")]

// Test friends - need access to internal test-only TrieSyncNode constructors and
// other internals for white-box testing.
[assembly: InternalsVisibleTo("Nethermind.Synchronization.Test")]
[assembly: InternalsVisibleTo("Nethermind.State.Test")]
[assembly: InternalsVisibleTo("Nethermind.Trie.Test")]
[assembly: InternalsVisibleTo("Nethermind.Trie.Benchmark")]
[assembly: InternalsVisibleTo("Nethermind.Blockchain.Test")]
