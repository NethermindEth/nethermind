// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Core;

namespace Nethermind.Shutter;

public interface IShutterBlockHandler : IDisposable
{
    Task<Block?> WaitForBlockInSlot(ulong slot, CancellationToken cancellationToken, Func<int, CancellationTokenSource>? initTimeoutSource = null);
}
