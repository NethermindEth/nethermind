// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;

namespace Nethermind.Db.Rocks;

internal sealed class MdbxReusableReadTransaction(MdbxEnvironment environment) : IDisposable
{
    private readonly MdbxEnvironment _environment = environment;
    private MdbxNative.SafeMdbxTxnHandle? _txn;
    private bool _active;

    public byte[]? Get(uint dbi, ReadOnlySpan<byte> key)
    {
        MdbxNative.SafeMdbxTxnHandle txn = Renew();
        bool suppressResetException = false;
        try
        {
            return _environment.Get(txn, dbi, key);
        }
        catch (Exception)
        {
            suppressResetException = true;
            throw;
        }
        finally
        {
            Reset(suppressResetException);
        }
    }

    public bool KeyExists(uint dbi, ReadOnlySpan<byte> key)
    {
        MdbxNative.SafeMdbxTxnHandle txn = Renew();
        bool suppressResetException = false;
        try
        {
            return _environment.KeyExists(txn, dbi, key);
        }
        catch (Exception)
        {
            suppressResetException = true;
            throw;
        }
        finally
        {
            Reset(suppressResetException);
        }
    }

    public void Dispose()
    {
        _active = false;
        _txn?.Dispose();
        _txn = null;
    }

    private MdbxNative.SafeMdbxTxnHandle Renew()
    {
        MdbxNative.SafeMdbxTxnHandle? txn = _txn;
        if (txn is null)
        {
            txn = _environment.BeginReadOnlyTransaction();
            _txn = txn;
        }
        else
        {
            int result = MdbxNative.TxnRenew(txn);
            if (result != MdbxNative.Success)
            {
                txn.Dispose();
                _txn = null;
                MdbxNative.ThrowOnError(result, "mdbx_txn_renew(readonly)");
            }
        }

        _active = true;
        return txn;
    }

    private void Reset(bool suppressResetException)
    {
        if (!_active || _txn is null)
        {
            return;
        }

        int result = MdbxNative.TxnReset(_txn);
        _active = false;
        if (result == MdbxNative.Success)
        {
            return;
        }

        _txn.Dispose();
        _txn = null;

        _environment.LogReadTransactionResetFailure(result);
        if (!suppressResetException)
        {
            MdbxNative.ThrowOnError(result, "mdbx_txn_reset(readonly)");
        }
    }
}
