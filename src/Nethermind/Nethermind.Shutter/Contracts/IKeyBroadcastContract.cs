// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;

namespace Nethermind.Shutter.Contracts;

public interface IKeyBroadcastContract
{
    /// <summary>
    /// Retrieves the public key for an eon.
    /// </summary>
    /// <param name="eon"></param>
    byte[] GetEonKey(BlockHeader blockHeader, in ulong eon);
}
