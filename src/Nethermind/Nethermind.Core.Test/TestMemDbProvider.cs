// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only


using System.Threading.Tasks;
using Autofac;
using Nethermind.Api;
using Nethermind.Blockchain.Synchronization;
using Nethermind.Config;
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
            builder.RegisterType<MemDbFactory>().As<IDbFactory>();
            builder.RegisterInstance(configProvider);
            return builder.Build().Resolve<IDbProvider>();
        }
    }
}
