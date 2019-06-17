using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Nethermind.DataMarketplace.Initializers
{
    public class NdmInitializerFactory
    {
        public INdmInitializer CreateOrFail(string initializer, string pluginsPath)
        {
            if (!string.IsNullOrWhiteSpace(pluginsPath) && Directory.Exists(pluginsPath))
            {
                var plugins = Directory.GetFiles(pluginsPath, "*.dll");
                foreach (var plugin in plugins)
                {
                    var fullPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, plugin);
                    Assembly.LoadFile(fullPath);
                }
            }

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