// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.Synchronization.FastSync
{
    public enum NodeProgressState
    {
        Unknown,
        Empty,
        Requested,
        AlreadySaved,
        Saved
    }
}
