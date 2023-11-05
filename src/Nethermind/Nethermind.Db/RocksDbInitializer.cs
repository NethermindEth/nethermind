// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Db
{
    public abstract class RocksDbInitializer
    {
        private readonly IDbProvider _dbProvider;
        protected IRocksDbFactory RocksDbFactory { get; }
        protected IMemDbFactory MemDbFactory { get; }
        protected bool PersistedDb => _dbProvider.DbMode == DbModeHint.Persisted;

        private readonly List<Action> _registrations = new();

        protected RocksDbInitializer(IDbProvider? dbProvider, IRocksDbFactory? rocksDbFactory, IMemDbFactory? memDbFactory)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            RocksDbFactory = rocksDbFactory ?? NullRocksDbFactory.Instance;
            MemDbFactory = memDbFactory ?? NullMemDbFactory.Instance;
        }

        protected void RegisterCustomDb(string dbName, Func<IDb> dbFunc)
        {
            void Action()
            {
                IDb db = dbFunc();
                _dbProvider.RegisterDb(dbName, db);
            }

            _registrations.Add(Action);
        }

        protected void RegisterCustomColumnDb<T>(string dbName, Func<IColumnsDb<T>> dbFunc)
        {
            void Action()
            {
                IColumnsDb<T> db = dbFunc();
                _dbProvider.RegisterColumnDb(dbName, db);
            }

            _registrations.Add(Action);
        }

        protected void RegisterDb(RocksDbSettings settings) =>
            AddRegisterAction(settings.DbName, () => CreateDb(settings));

        protected void RegisterColumnsDb<T>(RocksDbSettings settings) where T : struct, Enum =>
            AddRegisterAction(settings.DbName, () => CreateColumnDb<T>(settings));

        private void AddRegisterAction(string dbName, Func<IDb> dbCreation) =>
            _registrations.Add(() => _dbProvider.RegisterDb(dbName, dbCreation()));
        private void AddRegisterAction<T>(string dbName, Func<IColumnsDb<T>> dbCreation) =>
            _registrations.Add(() => _dbProvider.RegisterColumnDb(dbName, dbCreation()));

        private IDb CreateDb(RocksDbSettings settings) =>
            PersistedDb ? RocksDbFactory.CreateDb(settings) : MemDbFactory.CreateDb(settings.DbName);

        private IColumnsDb<T> CreateColumnDb<T>(RocksDbSettings settings) where T : struct, Enum =>
            PersistedDb ? RocksDbFactory.CreateColumnsDb<T>(settings) : MemDbFactory.CreateColumnsDb<T>(settings.DbName);

        protected void InitAll()
        {
            foreach (var registration in _registrations)
            {
                registration.Invoke();
            }
        }

        protected async Task InitAllAsync()
        {
            HashSet<Task> allInitializers = new();
            foreach (var registration in _registrations)
            {
                allInitializers.Add(Task.Run(() => registration.Invoke()));
            }

            await Task.WhenAll(allInitializers);
        }

        protected static string GetTitleDbName(string dbName) => char.ToUpper(dbName[0]) + dbName[1..];
    }
}
