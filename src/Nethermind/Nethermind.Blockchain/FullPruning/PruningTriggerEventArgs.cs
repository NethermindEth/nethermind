// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Blockchain.FullPruning;

public class PruningTriggerEventArgs : EventArgs
{
    /// <summary>
    /// Result of triggering Full Pruning
    /// </summary>
    public PruningStatus Status { get; set; }
}
