// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Synchronization
{
    public class Timeouts
    {
        public static readonly TimeSpan Eth = TimeSpan.FromSeconds(10);
        public static readonly TimeSpan RefreshDifficulty = TimeSpan.FromSeconds(8);
        public static readonly TimeSpan DefaultFetchHeaderTimeout = TimeSpan.FromSeconds(2);
    }
}
