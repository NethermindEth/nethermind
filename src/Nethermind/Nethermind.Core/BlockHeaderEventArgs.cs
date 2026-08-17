// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Core
{
    public class BlockHeaderEventArgs(BlockHeader header) : EventArgs
    {
        public BlockHeader Header { get; } = header;
    }
}
