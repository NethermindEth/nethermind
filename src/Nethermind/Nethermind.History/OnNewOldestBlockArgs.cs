// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;

namespace Nethermind.Blockchain;

public class OnNewOldestBlockArgs(BlockHeader oldest) : EventArgs
{
    public BlockHeader OldestBlockHeader = oldest;
}
