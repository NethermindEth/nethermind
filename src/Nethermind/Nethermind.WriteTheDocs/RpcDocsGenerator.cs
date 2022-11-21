// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.WriteTheDocs
{
    public class RpcDocsGenerator : IDocsGenerator
    {
        private static List<string> _assemblyNames = new List<string>
        {
            "Nethermind.Consensus.Clique",
            "Nethermind.JsonRpc"
        };

        public void Generate()
        {
            StringBuilder descriptionsBuilder = new StringBuilder(@"JSON RPC
********

JSON RPC is available via HTTP and WS (needs to be explicitly switched on in the InitConfig).
Some of the methods listed below are not implemented by Nethermind (they are marked).

");

            List<Type> jsonRpcModules = new List<Type>();

            foreach (string assemblyName in _assemblyNames)
            {
                Assembly assembly = Assembly.Load(new AssemblyName(assemblyName));
                foreach (Type type in assembly.GetTypes().Where(t => typeof(IModule).IsAssignableFrom(t)).Where(t => t.IsInterface && t != typeof(IModule)))
                {
                    jsonRpcModules.Add(type);
                }
            }

            foreach (Type jsonRpcModule in jsonRpcModules.OrderBy(t => t.Name))
            {
                string moduleName = jsonRpcModule.Name.Substring(1).Replace("Module", "").ToLowerInvariant();
                descriptionsBuilder.Append($@"{moduleName}
{string.Empty.PadLeft(moduleName.Length, '^')}

");

                var properties = jsonRpcModule.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                foreach (MethodInfo methodInfo in properties.OrderBy(p => p.Name))
                {
                    JsonRpcMethodAttribute attribute = methodInfo.GetCustomAttribute<JsonRpcMethodAttribute>();
                    string notImplementedString = attribute == null || attribute.IsImplemented ? string.Empty : "[NOT IMPLEMENTED] ";
                    descriptionsBuilder.AppendLine($" {methodInfo.Name}({string.Join(", ", methodInfo.GetParameters().Select(p => p.Name))})")
                        .AppendLine($"  {notImplementedString}{attribute?.Description ?? "<description missing>"}").AppendLine();
                }
            }

            string result = descriptionsBuilder.ToString();

            Console.WriteLine(result);
            File.WriteAllText("jsonrpc.rst", result);
            string sourceDir = DocsDirFinder.FindDocsDir();
            File.WriteAllText(Path.Combine(sourceDir, "jsonrpc.rst"), result);
        }
    }
}

