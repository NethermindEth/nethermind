// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Config;

/// <summary>Diagnostic formats written for blocks built by the block producer.</summary>
[Flags]
public enum ProducedBlockDumpOptions
{
    /// <summary>Disables produced block dumps.</summary>
    None = 0,
    /// <summary>Writes receipt traces.</summary>
    Receipts = 1,
    /// <summary>Writes Parity-style traces.</summary>
    Parity = 2,
    /// <summary>Writes Geth-style traces including EVM memory.</summary>
    Geth = 4,
    /// <summary>Writes the produced block as RLP to a file.</summary>
    Rlp = 8,
    /// <summary>Writes the produced block as RLP to the log.</summary>
    RlpLog = 16,
    /// <summary>Writes receipt traces and RLP files.</summary>
    Default = Receipts | Rlp,
    /// <summary>Writes every trace format and RLP files.</summary>
    All = Receipts | Parity | Geth | Rlp
}
