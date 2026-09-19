// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Pbt;

namespace Nethermind.State.Pbt;

/// <summary>One complete-key leaf record to include in a rebuilt EIP-8297 tree.</summary>
/// <remarks>Producers list a slot run's leaves consecutively and never end a chunk inside a run: the rebuilder folds a run whole.</remarks>
public readonly record struct RebuildEntry(PbtStorageTreeKey Key, ValueHash256 Leaf);
