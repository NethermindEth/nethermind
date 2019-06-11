using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Nethermind.Config;

namespace Nethermind.WriteTheDocs
{
    public class ConfigDocsGenerator : IDocsGenerator
    {
        private static List<string> _assemblyNames = new List<string>
        {
            "Nethermind.Blockchain",
            "Nethermind.Db",
            "Nethermind.JsonRpc",
            "Nethermind.KeyStore",
            "Nethermind.Network",
//            "Nethermind.Runner",
        };

        public void Generate()
        {
            StringBuilder descriptionsBuilder = new StringBuilder(@"Configuration
*************

");

            StringBuilder exampleBuilder = new StringBuilder(@"Sample configuration (mainnet)
^^^^^^^^^^^^^^^^^^^^^^^^^^^^^^

::

    [
");

            List<Type> configTypes = new List<Type>();

            foreach (string assemblyName in _assemblyNames)
            {
                Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
                foreach (Type type in assembly.GetTypes().Where(t => typeof(IConfig).IsAssignableFrom(t)).Where(t => !t.IsInterface))
                {
                    configTypes.Add(type);
                }
            }

            foreach (Type configType in configTypes.OrderBy(t => t.Name))
            {
                descriptionsBuilder.Append($@"{configType.Name}
{string.Empty.PadLeft(configType.Name.Length, '^')}

");

                exampleBuilder.AppendLine("      {");
                exampleBuilder.AppendLine($"        \"ConfigModule\": \"{configType.Name}\"");
                exampleBuilder.AppendLine("        \"ConfigItems\": {");

                var properties = configType.GetProperties(BindingFlags.Public | BindingFlags.Instance);
                foreach (PropertyInfo propertyInfo in properties.OrderBy(p => p.Name))
                {
                    exampleBuilder.AppendLine($"          \"{propertyInfo.Name}\" : example");
                    ConfigItemAttribute attribute = propertyInfo.GetCustomAttribute<ConfigItemAttribute>();
                    if (attribute == null)
                    {
                        descriptionsBuilder.AppendLine($" - {propertyInfo.Name} - description missing").AppendLine();
                        continue;
                    }

                    descriptionsBuilder.AppendLine($" - {propertyInfo.Name} - {attribute.Description}").AppendLine();
                }

                exampleBuilder.AppendLine("        }");
                exampleBuilder.AppendLine("      },");
            }

            exampleBuilder.AppendLine("    ]");

            string result = string.Concat(descriptionsBuilder.ToString(), exampleBuilder.ToString());

            Console.WriteLine(result);
            File.WriteAllText("configuration.rst", result);
        }
    }
}