// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Evm;

/// <summary>
/// A nested frame's RETURN/REVERT data copied into a rented array, recycled by the parent frame once the next
/// call replaces it (see <c>VirtualMachine.SetReturnDataBuffer</c>).
/// </summary>
internal sealed class PooledReturnData(byte[] array, int length)
{
    public byte[] Array => array;
    public ReadOnlyMemory<byte> Memory => new(array, 0, length);
}
