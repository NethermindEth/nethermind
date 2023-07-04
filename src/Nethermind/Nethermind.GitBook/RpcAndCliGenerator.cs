// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

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
    public class RpcAndCliGenerator
    {
        private readonly MarkdownGenerator _markdownGenerator;
        private readonly SharedContent _sharedContent;

        public RpcAndCliGenerator(MarkdownGenerator markdownGenerator, SharedContent sharedContent)
        {
            _markdownGenerator = markdownGenerator;
            _sharedContent = sharedContent;
        }

        public void Generate()
        {
            string docsDir = DocsDirFinder.FindDocsDir();
            RpcAndCliDataProvider rpcAndCliDataProvider = new RpcAndCliDataProvider();

            Dictionary<string, Dictionary<string, MethodData>> modulesData =
                rpcAndCliDataProvider.GetRpcAndCliData();

            foreach (string moduleName in modulesData.Keys)
            {
                modulesData.TryGetValue(moduleName, out var moduleMethods);
                GenerateDocFileContent(moduleName, moduleMethods, docsDir);
            }
        }

        private void GenerateDocFileContent(string moduleName, Dictionary<string, MethodData> moduleMethods, string docsDir)
        {
            StringBuilder rpcBuilder = new StringBuilder();
            StringBuilder cliBuilder = new StringBuilder();

            rpcBuilder.AppendLine($"# {moduleName}");
            rpcBuilder.AppendLine();

            cliBuilder.AppendLine($"# {moduleName}");
            cliBuilder.AppendLine();

            string[] methodsNames = moduleMethods.Keys.OrderBy(n => n).ToArray();
            foreach (string methodName in methodsNames)
            {
                moduleMethods.TryGetValue(methodName, out MethodData methodData);

                if (methodData == null)
                {
                    continue;
                }

                string rpcMethodName = $"{moduleName}_{methodName}";
                string cliMethodName = $"{moduleName}.{methodName}";


                ParameterInfo[] parameters = methodData.Parameters;
                List<string> defaultArguments = new List<string>();
                List<string> exampleArguments = new List<string>();
                List<Type> typesToDescribe = new List<Type>();

                StringBuilder paramBuilder = new StringBuilder();
                StringBuilder returnBuilder = new StringBuilder();
                StringBuilder objectsBuilder = new StringBuilder();


                if (parameters.Length > 0)
                {
                    paramBuilder.AppendLine("| Parameter | Type | Description |");
                    paramBuilder.AppendLine("| :--- | :--- | :--- |");
                    foreach (ParameterInfo parameter in parameters)
                    {
                        JsonRpcParameterAttribute parameterAttribute =
                            parameter.GetCustomAttribute<JsonRpcParameterAttribute>();
                        string parameterType = _sharedContent.GetTypeToWrite(parameter.ParameterType, typesToDescribe);
                        paramBuilder.AppendLine(
                            $"| {parameter.Name} | `{parameterType}` | {parameterAttribute?.Description ?? ""} |");
                        defaultArguments.Add(parameter.Name);
                        if (parameterAttribute?.ExampleValue?.Length > 0)
                            exampleArguments.Add(parameterAttribute.ExampleValue);
                    }
                }
                else
                {
                    paramBuilder.AppendLine("| This method doesn't have parameters. |");
                    paramBuilder.AppendLine("| :--- |");
                }

                string rpcInvocation = _markdownGenerator.GetRpcInvocationExample(rpcMethodName, defaultArguments);
                string cliInvocation = _markdownGenerator.GetCliInvocationExample(cliMethodName, defaultArguments, methodData.IsFunction);

                if (exampleArguments.Count != defaultArguments.Count)
                {
                    exampleArguments = defaultArguments;
                }


                if (methodData.InvocationType != InvocationType.Cli)
                {
                    bool isImplemented = methodData.IsImplemented ?? true;
                    if (!isImplemented)
                    {
                        continue;
                    }

                    Type returnType = GetTypeFromWrapper(methodData.ReturnType);
                    string returnTypeToWrite = _sharedContent.GetTypeToWrite(returnType, typesToDescribe);
                    returnBuilder.AppendLine("| Returned type | Description |");
                    returnBuilder.AppendLine("| :--- | :--- |");
                    returnBuilder.AppendLine(@$"| `{returnTypeToWrite}` | {methodData.ResponseDescription} |");

                    rpcBuilder.AppendLine($"## {rpcMethodName}");
                    TryAddDescription(rpcBuilder, methodData.Description);
                    TryAddHint(rpcBuilder, methodData.EdgeCaseHint);
                    cliBuilder.AppendLine();
                    rpcBuilder.AppendLine("| Invocation |");
                    rpcBuilder.AppendLine("| :--- |");
                    rpcBuilder.AppendLine($"| `{rpcInvocation}` |");
                    rpcBuilder.AppendLine();
                    rpcBuilder.Append(paramBuilder);
                    rpcBuilder.AppendLine();
                    rpcBuilder.Append(returnBuilder);
                    rpcBuilder.AppendLine();

                    _markdownGenerator.OpenTabs(rpcBuilder);
                    _markdownGenerator.CreateTab(rpcBuilder, $"Example request of {rpcMethodName}");
                    _markdownGenerator.CreateCurlExample(rpcBuilder, rpcMethodName, exampleArguments);
                    _markdownGenerator.CloseTab(rpcBuilder);

                    if (methodData.ExampleResponse != null)
                    {
                        string exampleResponse = CreateRpcExampleResponse(methodData.ExampleResponse);
                        _markdownGenerator.CreateTab(rpcBuilder, $"Example response of {rpcMethodName}");
                        _markdownGenerator.CreateCodeBlock(rpcBuilder, exampleResponse);
                        _markdownGenerator.CloseTab(rpcBuilder);
                    }

                    if (typesToDescribe.Count != 0)
                    {
                        objectsBuilder.AppendLine();
                        _markdownGenerator.CreateTab(objectsBuilder, $"Objects in {rpcMethodName}");
                        _sharedContent.AddObjectsDescription(objectsBuilder, typesToDescribe);
                        _markdownGenerator.CloseTab(objectsBuilder);
                    }

                    rpcBuilder.Append(objectsBuilder);
                    _markdownGenerator.CloseTabs(rpcBuilder);
                    rpcBuilder.AppendLine();

                    if (methodData.InvocationType == InvocationType.Both)
                    {
                        rpcBuilder.AppendLine($"[See also CLI {cliMethodName}](https://docs.nethermind.io/nethermind/nethermind-utilities/cli/{moduleName}#{moduleName.ToLower()}-{methodName.ToLower()})");

                        CreateCliContent(cliBuilder, paramBuilder, returnBuilder, objectsBuilder, cliMethodName, exampleArguments, cliInvocation, methodData);
                        cliBuilder.AppendLine($"[See also JSON RPC {rpcMethodName}](https://docs.nethermind.io/nethermind/ethereum-client/json-rpc/{moduleName}#{rpcMethodName.ToLower()})");
                    }
                }
                else
                {
                    Type returnType = methodData.ReturnType;
                    string returnTypeToWrite = _sharedContent.GetTypeToWrite(returnType, typesToDescribe);
                    returnBuilder.AppendLine("| Returned type | Description |");
                    returnBuilder.AppendLine("| :--- | :--- |");
                    returnBuilder.AppendLine(@$"| `{returnTypeToWrite}` | {methodData.ResponseDescription} |");

                    CreateCliContent(cliBuilder, paramBuilder, returnBuilder, objectsBuilder, cliMethodName, exampleArguments, cliInvocation, methodData);
                }
            }

            int emptyModuleLength = "# only_some_module_name".Length;

            if (rpcBuilder.Length > emptyModuleLength)
            {
                _sharedContent.Save(moduleName, string.Concat(docsDir, "/ethereum-client/json-rpc"), rpcBuilder);
            }
            if (cliBuilder.Length > emptyModuleLength)
            {
                _sharedContent.Save(moduleName, string.Concat(docsDir, "/nethermind-utilities/cli"), cliBuilder);
            }
        }

        private string CreateRpcExampleResponse(string methodDataExampleResponse)
        {
            string exampleResponseForRpc = methodDataExampleResponse.Replace("\n", "\n  ");
            return string.Concat("{\n  \"jsonrpc\": \"2.0\",\n  \"result\": ", exampleResponseForRpc, ",\n  \"id\": 1\n}");
        }

        private void CreateCliContent(StringBuilder cliBuilder, StringBuilder paramBuilder, StringBuilder returnBuilder, StringBuilder objectsBuilder, string cliMethodName, List<string> exampleArguments, string cliInvocation, MethodData methodData)
        {
            cliBuilder.AppendLine();
            cliBuilder.AppendLine($"## {cliMethodName}");
            TryAddDescription(cliBuilder, methodData.Description);
            TryAddHint(cliBuilder, methodData.EdgeCaseHint);
            cliBuilder.AppendLine();
            cliBuilder.AppendLine("| Invocation |");
            cliBuilder.AppendLine("| :--- |");
            cliBuilder.AppendLine($"| `{cliInvocation}` |");
            cliBuilder.AppendLine();
            cliBuilder.Append(paramBuilder);
            cliBuilder.AppendLine();
            cliBuilder.Append(returnBuilder);
            cliBuilder.AppendLine();

            _markdownGenerator.OpenTabs(cliBuilder);
            _markdownGenerator.CreateTab(cliBuilder, $"Example request of {cliMethodName}");

            string cliInvocationExample = _markdownGenerator.GetCliInvocationExample(cliMethodName, exampleArguments, methodData.IsFunction);
            _markdownGenerator.CreateCodeBlock(cliBuilder, cliInvocationExample);
            _markdownGenerator.CloseTab(cliBuilder);

            if (methodData.ExampleResponse != null)
            {
                _markdownGenerator.CreateTab(cliBuilder, $"Example response of {cliMethodName}");
                _markdownGenerator.CreateCodeBlock(cliBuilder, methodData.ExampleResponse);
                _markdownGenerator.CloseTab(cliBuilder);
            }

            if (objectsBuilder.Length != 0)
            {
                cliBuilder.Append(objectsBuilder);
            }

            _markdownGenerator.CloseTabs(cliBuilder);
            cliBuilder.AppendLine();
        }

        private void TryAddDescription(StringBuilder moduleBuilder, string description)
        {
            if (description != null)
            {
                moduleBuilder.AppendLine();
                moduleBuilder.AppendLine($"{description} ");
                moduleBuilder.AppendLine();
            }
        }

        private void TryAddHint(StringBuilder moduleBuilder, string hint)
        {
            if (hint?.Length > 0)
            {
                _markdownGenerator.CreateEdgeCaseHint(moduleBuilder, hint);
            }
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

            if (isNullableType)
            {
                return Nullable.GetUnderlyingType(returnType);
            }

            return returnType.IsArray ? returnType.GetElementType() : returnType;
        }
    }
}
