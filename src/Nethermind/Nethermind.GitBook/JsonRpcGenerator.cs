using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Nethermind.JsonRpc.Modules;
using System.Threading.Tasks;
using Nethermind.GitBook.Extensions;

namespace Nethermind.GitBook
{
    public class JsonRpcGenerator
    {
        private readonly MarkdownGenerator _markdownGenerator;
        private readonly SharedContent _sharedContent;

        public JsonRpcGenerator(MarkdownGenerator markdownGenerator, SharedContent sharedContent)
        {
           _markdownGenerator = markdownGenerator;
           _sharedContent = sharedContent;
        }

        public void Generate()
        {
            string docsDir = DocsDirFinder.FindJsonRpc();
            List<Type> rpcTypes = GetRpcModules();

            foreach (Type rpcType in rpcTypes)
            {
                GenerateDocFileContent(rpcType, docsDir);
            }
        }
        
        private List<Type> GetRpcModules()
        {
            Assembly assembly = Assembly.Load("Nethermind.JsonRpc");
            List<Type> jsonRpcModules = new List<Type>();
        
            foreach (Type type in assembly.GetTypes()
                .Where(t => typeof(IModule).IsAssignableFrom(t))
                .Where(t => t.IsInterface && t != typeof(IModule)))
            {
                jsonRpcModules.Add(type);
            }
            
            jsonRpcModules.Add( Assembly.Load("Nethermind.Consensus.Clique").GetTypes()
                .Where(t => typeof(IModule).IsAssignableFrom(t))
                .First(t => t.IsInterface && t != typeof(IModule)));
            
            return jsonRpcModules;
        }

        private void GenerateDocFileContent(Type rpcType, string docsDir)
        {
            StringBuilder docBuilder = new StringBuilder();

            string moduleName = rpcType.Name.Substring(1).Replace("Module", "");
            MethodInfo[] moduleMethods = rpcType.GetMethods().OrderBy(m => m.Name).ToArray();
            List<Type> rpcTypesToDescribe;

            docBuilder.AppendLine(@$"# {moduleName}");
            docBuilder.AppendLine();
            moduleName = moduleName.ToLower();

            foreach (MethodInfo method in moduleMethods)
            {
                JsonRpcMethodAttribute attribute = method.GetCustomAttribute<JsonRpcMethodAttribute>();
                bool isImplemented = attribute == null || attribute.IsImplemented;

                if (!isImplemented)
                {
                    continue;
                }

                string methodName = method.Name.Substring(method.Name.IndexOf('_'));
                docBuilder.AppendLine(@$"## {moduleName}{methodName}");
                docBuilder.AppendLine();
                docBuilder.AppendLine(@$"{attribute?.Description ?? ""} ");
                docBuilder.AppendLine();
                _markdownGenerator.OpenTabs(docBuilder);
                _markdownGenerator.CreateTab(docBuilder, "Request");
                docBuilder.AppendLine(@"### **Parameters**");
                docBuilder.AppendLine();

                ParameterInfo[] parameters = method.GetParameters();
                rpcTypesToDescribe = new List<Type>();

                if (parameters.Length == 0)
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
                        rpcParameterType = _sharedContent.GetTypeToWrite(parameter.ParameterType, rpcTypesToDescribe);
                        docBuilder.AppendLine($"| {parameter.Name} | `{rpcParameterType}` |");
                    }
                }

                _markdownGenerator.CloseTab(docBuilder);
                docBuilder.AppendLine();
                _markdownGenerator.CreateTab(docBuilder, "Response");
                docBuilder.AppendLine(@$"### Return type");
                docBuilder.AppendLine();

                Type returnType = GetTypeFromWrapper(method.ReturnType);
                string returnRpcType = _sharedContent.GetTypeToWrite(returnType, rpcTypesToDescribe);

                docBuilder.AppendLine(@$"`{returnRpcType}`");

                _markdownGenerator.CloseTab(docBuilder);

                if (rpcTypesToDescribe.Count != 0)
                {
                    docBuilder.AppendLine();
                    _markdownGenerator.CreateTab(docBuilder, "Object definitions");
                    _sharedContent.AddObjectsDescription(docBuilder, rpcTypesToDescribe);
                    _markdownGenerator.CloseTab(docBuilder);
                }

                _markdownGenerator.CloseTabs(docBuilder);
                docBuilder.AppendLine();
            }

            _sharedContent.Save(moduleName, docsDir + "/json-rpc-modules", docBuilder);
        }
        private Type GetTypeFromWrapper(Type resultWrapper)
        {
            Type returnType;

            //this is for situation when we have Task<ResultWrapper<T>> and we want to get T out of it 
            if (resultWrapper.GetGenericTypeDefinition() == typeof(Task<>))
            {
                returnType = resultWrapper.GetGenericArguments()[0].GetGenericArguments()[0];
            }
            //when we know that there is only ResultWrapper<T> and we want to return T
            else
            {
                returnType = resultWrapper.GetGenericArguments()[0];
            }

            bool isNullableType = returnType.IsNullable();

            if(isNullableType)
            {
                return Nullable.GetUnderlyingType(returnType);
            }
            
            return returnType.IsArray ? returnType.GetElementType() : returnType;
        }
    }
}
