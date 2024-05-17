// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
using Nethermind.Core.Test;
using Nethermind.Runner.Modules;
using NSubstitute;

namespace Nethermind.Db
{
    public class TestMemDbProvider
    {
        public static Task<IDbProvider> InitAsync()
        {
            return Task.FromResult(Init());
        }

        public static IDbProvider Init()
        {
            IInitConfig initConfig = new InitConfig() { };
            ISyncConfig syncConfig = new SyncConfig() { };
            IConfigProvider configProvider = Substitute.For<IConfigProvider>();
            configProvider.GetConfig<IInitConfig>().Returns(initConfig);
            configProvider.GetConfig<ISyncConfig>().Returns(syncConfig);

            ContainerBuilder builder = new ContainerBuilder();
            builder.RegisterModule(new DatabaseModule(configProvider));
            builder.RegisterModule(new BaseModule());
            builder.RegisterType<TestMemDbFactory>().As<IDbFactory>();
            builder.RegisterInstance(configProvider);
            return builder.Build().Resolve<IDbProvider>();
        }

        public class TestMemDbFactory : IDbFactory
        {
            private Dictionary<string, object> _createdDb = new Dictionary<string, object>();

            public IDb CreateDb(DbSettings dbSettings)
            {
                if (!_createdDb.ContainsKey(dbSettings.DbName))
                {
                    _createdDb.Add(dbSettings.DbName, new TestMemDb());
                }
                return (TestMemDb)_createdDb[dbSettings.DbName];
            }

            public IColumnsDb<T> CreateColumnsDb<T>(DbSettings dbSettings) where T : struct, Enum
            {
                if (!_createdDb.ContainsKey(dbSettings.DbName))
                {
                    _createdDb.Add(dbSettings.DbName, new TestMemColumnsDb<T>());
                }
                return (TestMemColumnsDb<T>)_createdDb[dbSettings.DbName];
            }
        }
    }
}
