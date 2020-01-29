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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Nethermind.Cli;
using Nethermind.Cli.Modules;

namespace Nethermind.WriteTheDocs
{
    public class CliDocsGenerator : IDocsGenerator
    {
        private static List<string> _assemblyNames = new List<string>
        {
            "Nethermind.Cli"
        };

        public void Generate()
        {
            StringBuilder descriptionsBuilder = new StringBuilder(@"CLI
***

CLI access is not currently included in the Nethermind launcher but will be added very soon.

");

            List<Type> cliModules = new List<Type>();

            foreach (string assemblyName in _assemblyNames)
            {
                Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
                foreach (Type type in assembly.GetTypes().Where(t => typeof(CliModuleBase).IsAssignableFrom(t)).Where(t => !t.IsInterface && !t.IsAbstract))
                {
                    if (!type.Name.Contains("Ndm"))
                    {
                        cliModules.Add(type);
                    }
                }
            }

            foreach (Type cliModule in cliModules.OrderBy(t => t.Name))
            {
                CliModuleAttribute moduleAttribute = cliModule.GetCustomAttribute<CliModuleAttribute>();
                descriptionsBuilder.Append($@"{moduleAttribute.ModuleName}
{string.Empty.PadLeft(moduleAttribute.ModuleName.Length, '^')}

");

                var properties = cliModule.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                foreach (MethodInfo methodInfo in properties.OrderBy(p => p.Name))
                {
                    CliPropertyAttribute propertyAttribute = methodInfo.GetCustomAttribute<CliPropertyAttribute>();
                    CliFunctionAttribute functionAttribute = methodInfo.GetCustomAttribute<CliFunctionAttribute>();

                    if (propertyAttribute != null)
                    {
                        descriptionsBuilder.AppendLine($" {propertyAttribute.ObjectName}.{propertyAttribute.PropertyName}")
                            .AppendLine($"  {propertyAttribute.Description ?? "<check JSON RPC docs>"}")
                            .AppendLine();
                    }

                    if (functionAttribute != null)
                    {
                        descriptionsBuilder.AppendLine($" {functionAttribute.ObjectName}.{functionAttribute.FunctionName}({string.Join(", ", methodInfo.GetParameters().Select(p => p.Name))})")
                            .AppendLine($"  {functionAttribute.Description ?? "<check JSON RPC docs>"}")
                            .AppendLine();
                    }
                }
            }

            string result = descriptionsBuilder.ToString();

            Console.WriteLine(result);
            string sourceDir = DocsDirFinder.FindDocsDir();
            File.WriteAllText(Path.Combine(sourceDir, "cli.rst"), result);
        }
    }
}