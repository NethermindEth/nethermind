// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization.Peers
{
    [Flags]
    public enum AllocationContexts : uint
    {
        None = 0,
        Headers = 1,
        Bodies = 2,
        Receipts = 4,
        Blocks = Headers | Bodies | Receipts,
        State = 8,
        Snap = 16,
        All = Headers | Bodies | Receipts | Blocks | State | Snap,
    }
}
