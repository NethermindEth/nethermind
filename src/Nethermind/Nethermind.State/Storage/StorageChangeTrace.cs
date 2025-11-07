// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

namespace Nethermind.State;

internal readonly struct StorageChangeTrace
{
    public static readonly StorageChangeTrace _zeroBytes = new(StorageTree.ZeroBytes, StorageTree.ZeroBytes);
    public static ref readonly StorageChangeTrace ZeroBytes => ref _zeroBytes;

    public StorageChangeTrace(byte[]? before, byte[]? after)
    {
        After = after ?? StorageTree.ZeroBytes;
        Before = before ?? StorageTree.ZeroBytes;
    }

    public StorageChangeTrace(byte[]? after)
    {
        After = after ?? StorageTree.ZeroBytes;
        Before = StorageTree.ZeroBytes;
        IsInitialValue = true;
    }

    public readonly byte[] Before;
    public readonly byte[] After;
    public readonly bool IsInitialValue;
}
