// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization.Blocks
{
    [Flags]
    public enum DownloaderOptions
    {
        None = 0,
        Process = 1,
        WithReceipts = 2,
        MoveToMain = 4,
        WithBodies = 8,
        // ReSharper disable once UnusedMember.Global
        All = 15
    }
}
