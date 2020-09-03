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
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Nethermind.Blockchain.Analytics;
using Nethermind.Grpc;
using Nethermind.Logging;
using Nethermind.Runner.Ethereum.Context;
using YamlDotNet.Serialization.TypeInspectors;

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

            public Task PublishAsync<T>(T data) where T : class
            {
                if (data != null)
                {
                    if (_logger.IsInfo) _logger.Info(data.ToString());
                }

                return Task.CompletedTask;
            }
        }

        private class GrpcPublisher : IDataPublisher
        {
            private readonly IGrpcServer _grpcServer;

            public GrpcPublisher(IGrpcServer grpcServer)
            {
                _grpcServer = grpcServer ?? throw new ArgumentNullException(nameof(grpcServer));
            }

            public Task PublishAsync<T>(T data) where T : class
            {
                if (data == null)
                {
                    return Task.CompletedTask;
                }
                
                return _grpcServer.PublishAsync(data, null);
            }
        }
        
        private class CompositePublisher : IDataPublisher
        {
            private readonly IDataPublisher[] _dataPublisher;

            public CompositePublisher(params IDataPublisher[] dataPublisher)
            {
                _dataPublisher = dataPublisher;
            }

            public async Task PublishAsync<T>(T data) where T : class
            {
                Task[] tasks = new Task[_dataPublisher.Length];
                for (int i = 0; i < _dataPublisher.Length; i++)
                {
                    tasks[i] = _dataPublisher[i].PublishAsync(data);
                }

                await Task.WhenAll(tasks);
            }
        }

        public virtual Task Execute(CancellationToken cancellationToken)
        {
            IInitConfig initConfig = _context.Config<IInitConfig>();
            IGrpcConfig grpcConfig = _context.Config<IGrpcConfig>();
            
            string fullPluginsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory!, initConfig.PluginsDirectory);
            if (!Directory.Exists(fullPluginsDir))
            {
                if (_logger.IsWarn) _logger.Warn($"Plugins folder {fullPluginsDir} was not found. Skipping.");
                return Task.CompletedTask;
            }
            
            string[] pluginFiles = Directory.GetFiles(fullPluginsDir).Where(p => p.EndsWith("dll")).ToArray();

            if (pluginFiles.Length > 0)
            {
                if (_logger.IsInfo) _logger.Info($"Loading {pluginFiles.Length} analytics plugins from {fullPluginsDir}");
            }

            foreach (string path in pluginFiles)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                
                if (_logger.IsInfo) _logger.Warn($"Loading assembly {path}");
                Assembly assembly = Assembly.LoadFile(Path.Combine(fullPluginsDir, path));
                foreach (Type type in assembly.GetTypes())
                {
                    AnalyticsLoaderAttribute? loader = type.GetCustomAttribute<AnalyticsLoaderAttribute>();
                    if (loader != null)
                    {
                        if(_logger.IsWarn) _logger.Warn($"Activating plugin {type.Name} from {path} {new FileInfo(path).CreationTime}");
                        IAnalyticsPluginLoader? pluginLoader = Activator.CreateInstance(type) as IAnalyticsPluginLoader;
                        if (grpcConfig.Enabled)
                        {
                            if(_logger.IsWarn) _logger.Warn($"Initializing gRPC for {type.Name}");
                            pluginLoader?.Init(_context.FileSystem, _context.TxPool, _context.BlockTree, _context.MainBlockProcessor, new GrpcPublisher(_context.GrpcServer!), _context.LogManager);
                        }
                        else
                        {
                            if(_logger.IsWarn) _logger.Warn($"Initializing log publisher for {type.Name}");
                            pluginLoader?.Init(_context.FileSystem, _context.TxPool, _context.BlockTree, _context.MainBlockProcessor, new LogDataPublisher(_context.LogManager), _context.LogManager);
                        }
                    }
                }
            }

            return Task.CompletedTask;
        }
    }
}