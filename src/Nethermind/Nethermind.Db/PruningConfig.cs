// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Db
{
    public class PruningConfig : IPruningConfig
    {
        public ulong PruningBoundary { get; set; } = Reorganization.MaxDepth;
    }
}
