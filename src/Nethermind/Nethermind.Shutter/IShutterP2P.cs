// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;

namespace Nethermind.Shutter;

public interface IShutterP2P
{
    void Start();
    public ValueTask DisposeAsync();
    event EventHandler<Dto.DecryptionKeys> KeysReceived;
}
