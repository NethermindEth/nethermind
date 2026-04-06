// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.State;

namespace Nethermind.Blockchain.FullPruning;

/// <summary>
/// When registered in DI, wraps the <see cref="IStateReader"/> used by <see cref="FullPruner"/>'s
/// trie copy pass. Allows plugins to intercept full pruning tree traversal without affecting other
/// <see cref="IStateReader"/> consumers (e.g. JSON-RPC modules).
/// </summary>
public record FullPrunerStateReaderFactory(Func<IStateReader, IStateReader> Wrap);
