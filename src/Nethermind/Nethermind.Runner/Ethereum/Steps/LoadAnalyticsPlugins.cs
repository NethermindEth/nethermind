//  Copyright (c) 2018 Demerzel Solutions Limited
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
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Nethermind.Grpc;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using Nethermind.TxPool.Analytics;

namespace Nethermind.Runner.Ethereum.Steps
{
    [RunnerStepDependencies(typeof(InitializeNetwork))]
    public class LoadAnalyticsPlugins : IStep
    {
        private readonly EthereumRunnerContext _context;
        private readonly ILogger _logger;

        public LoadAnalyticsPlugins(EthereumRunnerContext context)
        {
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _logger = context.LogManager.GetClassLogger();
        }

        private class LogDataPublisher : IDataPublisher
        {
            private readonly ILogger _logger;

            public LogDataPublisher(ILogManager logManager)
            {
                _logger = logManager.GetClassLogger();
            }

            public void Publish<T>(T data) where T : class
            {
                if (data == null)
                {
                    return;
                }

                if (_logger.IsInfo) _logger.Info(data.ToString());
            }
        }

        private class GrpcLogPublisher : IDataPublisher
        {
            private readonly IGrpcServer _grpcServer;
            private readonly ILogger _logger;

            public GrpcLogPublisher(IGrpcServer grpcServer, ILogManager logManager)
            {
                _grpcServer = grpcServer ?? throw new ArgumentNullException(nameof(grpcServer));
                _logger = logManager.GetClassLogger();
            }

            public void Publish<T>(T data) where T : class
            {
                if (data == null)
                {
                    return;
                }

                if (_logger.IsWarn) _logger.Warn($"Publishing data {data.ToString()}");
                _grpcServer.PublishAsync<T>(data, "analytics");

                if (_logger.IsInfo) _logger.Info(data.ToString());
            }
        }

        public virtual Task Execute()
        {
            _logger.Warn("Loading analytics plugins");
            IInitConfig initConfig = _context.Config<IInitConfig>();
            IGrpcConfig grpcConfig = _context.Config<IGrpcConfig>();

            foreach (string path in Directory.GetFiles(initConfig.PluginsDirectory))
            {
                if (path.EndsWith("dll"))
                {
                    _logger.Warn($"Loading assembly {path}");
                    Assembly assembly = Assembly.LoadFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, path));
                    foreach (Type type in assembly.GetTypes())
                    {
                        AnalyticsLoaderAttribute? loader = type.GetCustomAttribute<AnalyticsLoaderAttribute>();
                        if (loader != null)
                        {
                            _logger.Warn($"Activating pluging {type.Name}");
                            IAnalyticsPluginLoader? pluginLoader = Activator.CreateInstance(type) as IAnalyticsPluginLoader;
                            if (grpcConfig.Enabled && grpcConfig.ProducerEnabled)
                            {
                                _logger.Warn($"Initializing gRPC for {type.Name}");
                                pluginLoader?.Init(_context.FileSystem, _context.TxPool, new GrpcLogPublisher(_context.GrpcServer, _context.LogManager), _context.LogManager);
                            }
                            else
                            {
                                _logger.Warn($"Initializing log publisher for {type.Name}");
                                pluginLoader?.Init(_context.FileSystem, _context.TxPool, new LogDataPublisher(_context.LogManager), _context.LogManager);
                            }
                        }
                    }
                }
                else
                {
                    _logger.Warn($"Skipping {path}");
                }
            }

            return Task.CompletedTask;
        }
    }
}