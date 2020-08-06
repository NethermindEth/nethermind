using System.IO;
using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Nethermind.JsonRpc.Modules;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.GitBook
{
    public static class JsonRpcGenerator
    {
        public static void Generate()
        {
            string docsDir = DocsDirFinder.FindJsonRpc();
            List<Type> rpcTypes = GetRpcModules();
            
            foreach(Type rpcType in rpcTypes)
            {
                GenerateDocFileContent(rpcType, docsDir);
            }
        }

        private static List<Type> GetRpcModules()
        {
            Assembly assembly = Assembly.Load("Nethermind.JsonRpc");
            List<Type> jsonRpcModules = new List<Type>();

            foreach (Type type in assembly.GetTypes()
                    .Where(t => typeof(IModule).IsAssignableFrom(t))
                    .Where(t => t.IsInterface && t != typeof(IModule)))
            {
                jsonRpcModules.Add(type);
            }

            return jsonRpcModules;
        }

        private static void GenerateDocFileContent(Type rpcType, string docsDir)
        {
            StringBuilder docBuilder = new StringBuilder();

            string moduleName = rpcType.Name.Substring(1).Replace("Module", "").ToLower();
            MethodInfo[] moduleMethods = rpcType.GetMethods(BindingFlags.Public | BindingFlags.Instance);

            docBuilder.AppendLine(@$"#{moduleName}");
            docBuilder.AppendLine();

            foreach(MethodInfo method in moduleMethods)
            {
                JsonRpcMethodAttribute attribute = method.GetCustomAttribute<JsonRpcMethodAttribute>();
                bool isImplemented = attribute == null || attribute.IsImplemented;

                if(!isImplemented)
                {
                    continue;
                }

                string methodName = method.Name.Substring(method.Name.IndexOf('_'));
                docBuilder.AppendLine(@$"##{moduleName}\{methodName}");
                docBuilder.AppendLine();
                docBuilder.AppendLine(@$"{attribute?.Description ?? "_description missing_"} ");
                docBuilder.AppendLine();
                docBuilder.AppendLine(@"#### **Parameters**");
                docBuilder.AppendLine();
        
                ParameterInfo[] parameters = method.GetParameters();

                if(parameters.Length == 0)
                {
                    docBuilder.AppendLine("_None_");
                }
                else
                {
                    docBuilder.AppendLine("| Parameter name | Type |");
                    docBuilder.AppendLine("| :--- | :--- |");
                    string rpcParameterType;
                    foreach (ParameterInfo parameter in method.GetParameters())
                    {
                        rpcParameterType = GetJsonRpcType(parameter.ParameterType);
                        docBuilder.AppendLine($"| {parameter.Name} | `{rpcParameterType}` |");
                    }
                }
                
                docBuilder.AppendLine();
                docBuilder.AppendLine(@$"Return type: `{attribute?.Returns}`");
                docBuilder.AppendLine();
            }

            string rpcModuleFile = Directory.GetFiles(docsDir, $"{moduleName}.md", SearchOption.AllDirectories).First(); 

            string fileContent = docBuilder.ToString();
            File.WriteAllText(rpcModuleFile, fileContent);
        }

        private static string GetJsonRpcType(object parameter)
        {
            switch(parameter)
            {
                case Keccak _: 
                    return "Hash";
                case Address _:
                    return  "Address";
                case int _:
                    return "Quantity";
                case long _:
                    return "Quantity";
                case byte[] _:
                    return "Data";
                case string _:
                    return "String";
                case bool _:
                    return "Boolean";
                case UInt256 _: 
                    return "Quantity";
                default: 
                    return "Object";
            }
        }
    }
}