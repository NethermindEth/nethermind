// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.BeaconChain.Storage;

public enum BeaconChainDbColumns
{
    /// <summary>Beacon block root to snappy-compressed SSZ <c>SignedBeaconBlock</c>.</summary>
    Blocks,
    /// <summary>Slot (big-endian) to canonical beacon block root.</summary>
    BlockIndex,
    /// <summary>Beacon block root to snappy-compressed SSZ <c>BeaconState</c> snapshot.</summary>
    States,
    /// <summary>Fork choice store persistence: proto-array nodes, latest messages, checkpoints.</summary>
    ForkChoice,
    /// <summary>Anchor checkpoint, genesis validators root, ENR sequence, pubkey cache, schema version.</summary>
    Metadata,
}
