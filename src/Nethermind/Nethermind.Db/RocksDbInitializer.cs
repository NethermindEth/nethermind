//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Nethermind.Db
{
    public abstract class RocksDbInitializer
    {
        private readonly IDbProvider _dbProvider;
        private readonly IRocksDbFactory _rocksDbFactory;
        private readonly IMemDbFactory _memDbFactory;
        private readonly List<Action> _registrations = new();

        protected RocksDbInitializer(IDbProvider? dbProvider, IRocksDbFactory? rocksDbFactory, IMemDbFactory? memDbFactory)
        {
            _dbProvider = dbProvider ?? throw new ArgumentNullException(nameof(dbProvider));
            _rocksDbFactory = rocksDbFactory ?? NullRocksDbFactory.Instance;
            _memDbFactory = memDbFactory ?? NullMemDbFactory.Instance;
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

        protected void RegisterDb(RocksDbSettings settings)
        {
            AddRegisterAction(settings, () => _rocksDbFactory.CreateDb(settings), () => _memDbFactory.CreateDb(settings.DbName));
        }

        protected void RegisterColumnsDb<T>(RocksDbSettings settings)
        {
            AddRegisterAction(settings, () => _rocksDbFactory.CreateColumnsDb<T>(settings), () => _memDbFactory.CreateColumnsDb<T>(settings.DbName));
        }

        private void AddRegisterAction(RocksDbSettings settings, Func<IDb> rocksDbCreation, Func<IDb> memDbCreation)
        {
            var action = new Action(() =>
            {
                IDb db;
                if (_dbProvider.DbMode == DbModeHint.Persisted)
                    db = rocksDbCreation();
                else
                    db = memDbCreation();

                _dbProvider.RegisterDb(settings.DbName, db);
            });

            _registrations.Add(action);
        }

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

        protected static string GetTitleDbName(string dbName)
        {
            return char.ToUpper(dbName[0]) + dbName.Substring(1);
        }
    }
}
