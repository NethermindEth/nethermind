// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading;
using Nethermind.Core;

namespace Nethermind.Db;

public class CycleDb(IDb? db, DbSettings settings, IDbFactory dbFactory) : ICycleDb, ITunableDb
{
    private readonly ReaderWriterLockSlim _lock = new();

    public void Cycle()
    {
        _lock.EnterWriteLock();
        try
        {
            IDb db1 = db;
            db = null;
            db1?.Dispose();
            db = dbFactory.CreateDb(settings);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public byte[]? Get(ReadOnlySpan<byte> key, ReadFlags flags = ReadFlags.None)
    {
        IDb? db1 = db;
        if (db1 is not null)
        {
            try
            {
                return db1.Get(key, flags);
            }
            catch (ObjectDisposedException) { }
        }

        _lock.EnterReadLock();
        try
        {
            return Get(key, flags);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Set(ReadOnlySpan<byte> key, byte[]? value, WriteFlags flags = WriteFlags.None)
    {
        IDb? db1 = db;
        if (db1 is not null)
        {
            try
            {
                db1.Set(key, value, flags);
                return;
            }
            catch (ObjectDisposedException) { }
        }

        _lock.EnterReadLock();
        try
        {
            Set(key, value, flags);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IWriteBatch StartWriteBatch()
    {
        IDb? db1 = db;
        if (db1 is not null)
        {
            try
            {
                return db1.StartWriteBatch();
            }
            catch (ObjectDisposedException) { }
        }

        _lock.EnterReadLock();
        try
        {
            return StartWriteBatch();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Dispose()
    {
        IDb? db1 = db;
        if (db1 is not null)
        {
            try
            {
                db1.Dispose();
            }
            catch (ObjectDisposedException) { }
        }
    }

    public string Name
    {
        get
        {
            IDb? db1 = db;
            if (db1 is not null)
            {
                try
                {
                    return db1.Name;
                }
                catch (ObjectDisposedException) { }
            }

            _lock.EnterReadLock();
            try
            {
                return Name;
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public KeyValuePair<byte[], byte[]?>[] this[byte[][] keys]
    {
        get
        {
            IDb? db1 = db;
            if (db1 is not null)
            {
                try
                {
                    return db1[keys];
                }
                catch (ObjectDisposedException) { }
            }

            _lock.EnterReadLock();
            try
            {
                return this[keys];
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }
    }

    public IEnumerable<KeyValuePair<byte[], byte[]?>> GetAll(bool ordered = false)
    {
        IDb? db1 = db;
        if (db1 is not null)
        {
            try
            {
                return db1.GetAll(ordered);
            }
            catch (ObjectDisposedException) { }
        }

        _lock.EnterReadLock();
        try
        {
            return GetAll(ordered);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IEnumerable<byte[]> GetAllKeys(bool ordered = false)
    {
        IDb? db1 = db;
        if (db1 is not null)
        {
            try
            {
                return db1.GetAllKeys(ordered);
            }
            catch (ObjectDisposedException) { }
        }

        _lock.EnterReadLock();
        try
        {
            return GetAllKeys(ordered);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public IEnumerable<byte[]> GetAllValues(bool ordered = false)
    {
        IDb? db1 = db;
        if (db1 is not null)
        {
            try
            {
                return db1.GetAllValues(ordered);
            }
            catch (ObjectDisposedException) { }
        }

        _lock.EnterReadLock();
        try
        {
            return GetAllValues(ordered);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    public void Tune(ITunableDb.TuneType type)
    {
        if (db is ITunableDb tunableDb)
        {
            tunableDb.Tune(type);
        }
    }
}
