// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Shutter;

public interface IShutterP2P
{
    Task Start(Func<Dto.DecryptionKeys, Task> onKeysReceived, CancellationTokenSource? cts = null);
    ValueTask DisposeAsync();
}
