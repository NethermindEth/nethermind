// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.DataMarketplace.Consumers.Infrastructure;
using Nethermind.DataMarketplace.Infrastructure.Modules;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Initializers
{
    public class NdmInitializerFactory : INdmInitializerFactory
    {
        private readonly Type _initializerType;
        private readonly INdmModule _module;
        private readonly INdmConsumersModule _consumersModule;
        private readonly ILogManager _logManager;
        private readonly ILogger _logger;

        public NdmInitializerFactory(
            Type initializerType,
            INdmModule module,
            INdmConsumersModule consumersModule,
            ILogManager logManager)
        {
            _initializerType = initializerType ?? throw new ArgumentNullException(nameof(initializerType));
            _module = module ?? throw new ArgumentNullException(nameof(module));
            _consumersModule = consumersModule ?? throw new ArgumentNullException(nameof(consumersModule));
            _logManager = logManager ?? throw new ArgumentNullException(nameof(logManager));
            _logger = logManager.GetClassLogger();
        }

        public INdmInitializer CreateOrFail()
        {
            if (_initializerType is null)
            {
                throw new ArgumentNullException(nameof(_initializerType), $"NDM initializer type cannot be null.");
            }

            var name = _initializerType.Name;
            if (!typeof(INdmInitializer).IsAssignableFrom(_initializerType))
            {
                throw new MissingMethodException($"NDM initializer type: {_initializerType.Name}/{name} is not valid.", nameof(_initializerType));
            }

            if (_logger.IsInfo) _logger.Info($"Loading NDM using the initializer: {name}");
            var instance = Activator.CreateInstance(_initializerType, _module, _consumersModule, _logManager);
            if (instance is INdmInitializer initializer)
            {
                return initializer;
            }

            throw new ArgumentException($"NDM initializer type: {name} is not valid.", nameof(initializer));
        }
    }
}
