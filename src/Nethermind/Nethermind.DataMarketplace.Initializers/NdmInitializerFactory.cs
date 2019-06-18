using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Initializers
{
    public class NdmInitializerFactory
    {
        private readonly ILogger _logger;

        public NdmInitializerFactory(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
        }

        public INdmInitializer CreateOrFail(string initializer, string pluginsPath)
        {
            if (_logger.IsInfo) _logger.Info($"Loading NDM using the initializer: {initializer}");
            var ndmInitializerType = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(a => a.GetTypes())
                .FirstOrDefault(t => t.GetCustomAttribute<NdmInitializerAttribute>()?.Name == initializer);
            if (ndmInitializerType is null)
            {
                throw new ArgumentException($"NDM initializer type: {initializer} was not found.",
                    nameof(ndmInitializerType));
            }

            if (!(Activator.CreateInstance(ndmInitializerType) is INdmInitializer ndmInitializer))
            {
                throw new ArgumentException($"NDM initializer type: {initializer} is not valid.",
                    nameof(ndmInitializer));
            }

            return ndmInitializer;
        }
    }
}