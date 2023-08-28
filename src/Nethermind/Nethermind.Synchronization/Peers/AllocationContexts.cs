// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization.Peers
{
    [Flags]
    public enum AllocationContexts
    {
        None = 0,
        Headers = 1,
        Bodies = 2,
        Receipts = 4,
        Blocks = 7,
        State = 8,
        Witness = 16,
        Snap = 32,
        Verkle = 64,
        All = Headers | Bodies | Receipts | Blocks | State | Witness | Snap | Verkle
    }
}
