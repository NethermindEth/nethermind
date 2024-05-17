// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Core;

/// <summary>
/// Utility class to help configuring dependencies. They name here is expected to be registered in some way so that
/// it is easy for component to use it through constructor. Its usually from configs, but sometime it can be from
/// other components or combination of them. Used to differentiate between same time of dependency.
/// </summary>
public enum ComponentKey
{
    // Used by pruning trigger to determine which directory to watch
    FullPruningDbPath,
    FullPruningThresholdMb,

    // Used by testing. For skipping genesis in TestBlockchain
    SkipLoadGenesis,

    // Used by the two type of key
    // NodeKey for enode and such
    NodeKey,
    // Key used for signing blocks. Original as its loaded on startup. This can later be changed via RPC in <see cref="Signer"/>.
    SignerKey,

    // Mark the main world state. Since trie store have one distinct world state and it can't be shared.
    MainWorldState,

    UseCompactReceiptStore
}
