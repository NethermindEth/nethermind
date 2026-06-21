// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Core;

namespace Nethermind.Db.Rocks;

internal static class MdbxCursorHelpers
{
    public static IEnumerable<KeyValuePair<byte[], byte[]?>> Enumerate(MdbxEnvironment environment, uint dbi)
    {
        using MdbxNative.SafeMdbxTxnHandle txn = environment.BeginReadOnlyTransaction();
        using MdbxNative.SafeMdbxCursorHandle cursor = OpenCursor(txn, dbi);

        MdbxValue key = default;
        MdbxValue data = default;
        int result = MdbxNative.CursorGet(cursor, ref key, ref data, MdbxCursorOp.First);
        while (result == MdbxNative.Success)
        {
            yield return new KeyValuePair<byte[], byte[]?>(MdbxEnvironment.Copy(key), MdbxEnvironment.Copy(data));
            result = MdbxNative.CursorGet(cursor, ref key, ref data, MdbxCursorOp.Next);
        }

        if (result != MdbxNative.NotFound)
        {
            MdbxNative.ThrowOnError(result, "mdbx_cursor_get");
        }
    }

    public static byte[]? GetEdge(MdbxNative.SafeMdbxTxnHandle txn, uint dbi, MdbxCursorOp operation)
    {
        using MdbxNative.SafeMdbxCursorHandle cursor = OpenCursor(txn, dbi);
        MdbxValue key = default;
        MdbxValue data = default;
        int result = MdbxNative.CursorGet(cursor, ref key, ref data, operation);
        if (result == MdbxNative.NotFound)
        {
            return null;
        }

        MdbxNative.ThrowOnError(result, "mdbx_cursor_get");
        return MdbxEnvironment.Copy(key);
    }

    public static MdbxNative.SafeMdbxCursorHandle OpenCursor(MdbxNative.SafeMdbxTxnHandle txn, uint dbi)
    {
        MdbxNative.ThrowOnError(MdbxNative.CursorOpen(txn, dbi, out MdbxNative.SafeMdbxCursorHandle cursor), "mdbx_cursor_open");
        return cursor;
    }

    public static int Compare(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        int commonLength = Math.Min(left.Length, right.Length);
        for (int i = 0; i < commonLength; i++)
        {
            int comparison = left[i].CompareTo(right[i]);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }
}

internal sealed class MdbxSortedView : ISortedView
{
    private readonly MdbxNative.SafeMdbxTxnHandle _txn;
    private readonly MdbxNative.SafeMdbxCursorHandle _cursor;
    private readonly byte[] _lowerBound;
    private readonly byte[] _upperBound;
    private readonly bool _ownsTransaction;
    private bool _started;
    private byte[] _currentKey = [];
    private byte[] _currentValue = [];

    public MdbxSortedView(MdbxEnvironment environment, uint dbi, ReadOnlySpan<byte> firstKeyInclusive, ReadOnlySpan<byte> lastKeyExclusive)
        : this(environment.BeginReadOnlyTransaction(), dbi, firstKeyInclusive, lastKeyExclusive, ownsTransaction: true)
    {
    }

    public MdbxSortedView(
        MdbxNative.SafeMdbxTxnHandle txn,
        uint dbi,
        ReadOnlySpan<byte> firstKeyInclusive,
        ReadOnlySpan<byte> lastKeyExclusive,
        bool ownsTransaction)
    {
        _lowerBound = firstKeyInclusive.ToArray();
        _upperBound = lastKeyExclusive.ToArray();
        _txn = txn;
        _ownsTransaction = ownsTransaction;
        _cursor = MdbxCursorHelpers.OpenCursor(_txn, dbi);
    }

    public ReadOnlySpan<byte> CurrentKey => _currentKey;

    public ReadOnlySpan<byte> CurrentValue => _currentValue;

    public bool StartBefore(ReadOnlySpan<byte> value)
    {
        byte[] target = value.ToArray();
        if (!TryPositionBefore(target, out MdbxValue key, out MdbxValue data))
        {
            _started = false;
            return false;
        }

        byte[] foundKey = MdbxEnvironment.Copy(key);
        if (_upperBound.Length != 0 && MdbxCursorHelpers.Compare(foundKey, _upperBound) >= 0)
        {
            if (!TryPositionBefore(_upperBound, out key, out data))
            {
                _started = false;
                return false;
            }

            foundKey = MdbxEnvironment.Copy(key);
        }

        if (MdbxCursorHelpers.Compare(foundKey, _lowerBound) < 0 ||
            (_upperBound.Length != 0 && MdbxCursorHelpers.Compare(foundKey, _upperBound) >= 0))
        {
            _started = false;
            return false;
        }

        _currentKey = foundKey;
        _currentValue = MdbxEnvironment.Copy(data);
        _started = true;
        return true;
    }

    public bool MoveNext()
    {
        MdbxValue key;
        MdbxValue data = default;
        int result;

        if (_started)
        {
            key = default;
            result = MdbxNative.CursorGet(_cursor, ref key, ref data, MdbxCursorOp.Next);
        }
        else
        {
            if (_lowerBound.Length == 0)
            {
                key = default;
                result = MdbxNative.CursorGet(_cursor, ref key, ref data, MdbxCursorOp.First);
            }
            else
            {
                // MDBX only reads the lower-bound key during cursor positioning; the buffer is pinned for that call.
                unsafe
                {
                    fixed (byte* keyPointer = _lowerBound)
                    {
                        key = new MdbxValue { Base = (IntPtr)keyPointer, Length = (nuint)_lowerBound.Length };
                        result = MdbxNative.CursorGet(_cursor, ref key, ref data, MdbxCursorOp.SetRange);
                    }
                }
            }
        }

        if (result == MdbxNative.NotFound)
        {
            return false;
        }

        MdbxNative.ThrowOnError(result, "mdbx_cursor_get");
        byte[] nextKey = MdbxEnvironment.Copy(key);
        if (_upperBound.Length != 0 && MdbxCursorHelpers.Compare(nextKey, _upperBound) >= 0)
        {
            return false;
        }

        _currentKey = nextKey;
        _currentValue = MdbxEnvironment.Copy(data);
        _started = true;
        return true;
    }

    private bool TryPositionBefore(byte[] target, out MdbxValue key, out MdbxValue data)
    {
        key = default;
        data = default;

        if (target.Length == 0)
        {
            return false;
        }

        int result;
        // MDBX only reads the target key during cursor positioning; the buffer is pinned for that call.
        unsafe
        {
            fixed (byte* keyPointer = target)
            {
                key = new MdbxValue { Base = (IntPtr)keyPointer, Length = (nuint)target.Length };
                result = MdbxNative.CursorGet(_cursor, ref key, ref data, MdbxCursorOp.SetRange);
            }
        }

        if (result == MdbxNative.NotFound)
        {
            result = MdbxNative.CursorGet(_cursor, ref key, ref data, MdbxCursorOp.Last);
            if (result == MdbxNative.NotFound)
            {
                return false;
            }

            MdbxNative.ThrowOnError(result, "mdbx_cursor_get");
            return true;
        }

        MdbxNative.ThrowOnError(result, "mdbx_cursor_get");
        result = MdbxNative.CursorGet(_cursor, ref key, ref data, MdbxCursorOp.Prev);
        if (result == MdbxNative.NotFound)
        {
            return false;
        }

        MdbxNative.ThrowOnError(result, "mdbx_cursor_get");
        return true;
    }

    public void Dispose()
    {
        _cursor.Dispose();
        if (_ownsTransaction)
        {
            _txn.Dispose();
        }
    }
}
