// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Shutter;
public interface IShutterBlockHandler : IDisposable
{
    void OnNewHeadBlock(Block head);
    Task<Block?> WaitForBlockInSlot(ulong slot, TimeSpan slotLength, TimeSpan cutoff, CancellationToken cancellationToken);
}
