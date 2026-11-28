// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.State;

/// <summary>
/// A state read that cannot be served and that no retry can recover — as opposed to transient gather failures, which
/// remain plain <see cref="InvalidOperationException"/>. Covers both state this node does not hold and history rows
/// that cannot be trusted. The state readers wrap it into <see cref="Nethermind.Trie.MissingTrieNodeException"/>,
/// which JSON-RPC answers with resource-not-found (-32000) and a WARN; only <see cref="StateNotRetainedException"/>
/// opts in to resource-unavailable (-32002).
/// </summary>
public class StateUnavailableException(string message) : InvalidOperationException(message);
