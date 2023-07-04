// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization.FastSync
{
    [Flags]
    public enum NodeDataType
    {
        None = 0,
        Code = 1,
        State = 2,
        Storage = 4,
        All = 7
    }
}
