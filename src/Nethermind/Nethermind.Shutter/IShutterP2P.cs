// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading;
using System.Threading.Tasks;

namespace Nethermind.Shutter;

public interface IShutterP2P
{
    void Start(CancellationTokenSource? cts = null);
    public ValueTask DisposeAsync();
    event EventHandler<KeysReceivedArgs> KeysReceived;

    public class KeysReceivedArgs(Dto.DecryptionKeys keys) : EventArgs
    {
        public Dto.DecryptionKeys Keys = keys;
    }
}
