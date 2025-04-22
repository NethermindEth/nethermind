// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Trie.Pruning;

public interface IStoreWithReorgBoundary
{
    event EventHandler<ReorgBoundaryReached>? ReorgBoundaryReached;
}
