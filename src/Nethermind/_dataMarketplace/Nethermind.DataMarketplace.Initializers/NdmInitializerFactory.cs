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
            if(!typeof(INdmInitializer).IsAssignableFrom(_initializerType))
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