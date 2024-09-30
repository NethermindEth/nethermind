// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Logging;
using Nethermind.Trie.Pruning;

namespace Nethermind.State;

// TODO improve naming?
public class OverridableWorldState : WorldState
{
    private readonly OverlayTrieStore _trieStore;

    public OverridableWorldState(
        OverlayTrieStore trieStore, IKeyValueStore? codeDb, ILogManager? logManager
    ) : base(trieStore, codeDb, logManager) =>
        _trieStore = trieStore;

    public OverridableWorldState(
        OverlayTrieStore trieStore, IKeyValueStore? codeDb, ILogManager? logManager, PreBlockCaches? preBlockCaches,
        bool populatePreBlockCache = true
    ) : base(trieStore, codeDb, logManager, preBlockCaches, populatePreBlockCache) =>
        _trieStore = trieStore;

    /// <summary>
    /// Resets changes applied via <see cref="Nethermind.Evm.StateOverridesExtensions.ApplyStateOverrides"/>
    /// </summary>
    public void ResetOverrides() => _trieStore.ResetOverrides();
}
