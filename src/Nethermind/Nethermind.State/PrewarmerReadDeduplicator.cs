// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.State;

public sealed class PrewarmerReadDeduplicator
{
    private const int LockCount = 1 << 14;
    private const int LockMask = LockCount - 1;

    private readonly Lock[] _accountLocks = CreateLocks();
    private readonly Lock[] _storageLocks = CreateLocks();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Lock GetAccountLock(in AddressAsKey address)
        => _accountLocks[(int)address.GetHashCode64() & LockMask];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Lock GetStorageLock(in StorageCell storageCell)
        => _storageLocks[(int)storageCell.GetHashCode64() & LockMask];

    private static Lock[] CreateLocks()
    {
        Lock[] locks = new Lock[LockCount];
        for (int i = 0; i < locks.Length; i++)
        {
            locks[i] = new Lock();
        }

        return locks;
    }
}
