// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Pbt;

/// <summary>The settings one <see cref="TrieUpdater.UpdateRoot"/> call runs under.</summary>
/// <param name="WriteFormat">
/// Which encoding to write the groups the batch rebuilds in. Both fold to the same root and both are
/// read whatever this says, so it may change between batches over one store: a group is converted only
/// by a change that rewrites it anyway.
/// </param>
/// <param name="Parallelism">
/// How many threads the descent may spread its subtrees over. One keeps it wholly on the calling thread,
/// which is what a store that cannot take concurrent reads while the descent is in flight requires.
/// </param>
public readonly record struct PbtUpdateOptions(PbtGroupFormat WriteFormat, int Parallelism);
