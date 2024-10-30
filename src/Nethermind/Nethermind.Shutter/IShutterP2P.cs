// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;
using Multiformats.Address;

namespace Nethermind.Shutter;

public interface IShutterP2P
{
    Task Start(Multiaddress[] bootnodeP2PAddresses, Func<Dto.DecryptionKeys, Task> onKeysReceived, CancellationTokenSource? cts = null);
    ValueTask DisposeAsync();
}
