// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nethermind.Cli.Modules;
using Nethermind.JsonRpc.Modules;

namespace Nethermind.GitBook
{
    public class RpcAndCliDataProvider
    {
        private Dictionary<string, Dictionary<string, MethodData>> _modulesData =
            new Dictionary<string, Dictionary<string, MethodData>>();


        public Dictionary<string, Dictionary<string, MethodData>> GetRpcAndCliData()
        {
            List<Type> rpcTypes = GetRpcModules();
            List<Type> cliTypes = GetCliModules();
            AddRpcModulesData(rpcTypes);
            AddCliModulesData(cliTypes);

            return _modulesData;
        }

        private List<Type> GetRpcModules()
        {
            Assembly assembly = Assembly.Load("Nethermind.JsonRpc");
            List<Type> jsonRpcModules = new List<Type>();

            foreach (Type type in assembly.GetTypes()
                .Where(t => typeof(IRpcModule).IsAssignableFrom(t))
                .Where(t => !typeof(IContextAwareRpcModule).IsAssignableFrom(t))
                .Where(t => t.IsInterface && t != typeof(IRpcModule)))
            {
                jsonRpcModules.Add(type);
            }

            jsonRpcModules.Add(Assembly.Load("Nethermind.Consensus.Clique").GetTypes()
                .Where(t => typeof(IRpcModule).IsAssignableFrom(t))
                .First(t => t.IsInterface && t != typeof(IRpcModule)));

            return jsonRpcModules;
        }

        private List<Type> GetCliModules()
        {
            Assembly assembly = Assembly.Load("Nethermind.Cli");
            List<Type> cliModules = new List<Type>();

            foreach (Type type in assembly.GetTypes()
                .Where(t => typeof(CliModuleBase).IsAssignableFrom(t))
                .Where(t => t != typeof(CliModuleBase)))
            {
                cliModules.Add(type);
            }
            return cliModules;
        }

        private void AddRpcModulesData(List<Type> rpcTypes)
        {
            foreach (Type rpcType in rpcTypes)
            {
                string rpcModuleName = rpcType.Name.Substring(1).Replace("RpcModule", "").ToLower();

                MethodInfo[] moduleMethods = rpcType.GetMethods();
                Dictionary<string, MethodData> methods =
                    new Dictionary<string, MethodData>();

                foreach (MethodInfo moduleMethod in moduleMethods)
                {
                    string methodName = moduleMethod.Name.Substring(moduleMethod.Name.IndexOf('_') + 1);
                    JsonRpcMethodAttribute methodAttribute = moduleMethod.GetCustomAttribute<JsonRpcMethodAttribute>();

                    MethodData methodData = new MethodData()
                    {
                        IsImplemented = methodAttribute?.IsImplemented,
                        ReturnType = moduleMethod.ReturnType,
                        Parameters = moduleMethod.GetParameters(),
                        Description = methodAttribute?.Description,
                        EdgeCaseHint = methodAttribute?.EdgeCaseHint,
                        ResponseDescription = methodAttribute?.ResponseDescription,
                        ExampleResponse = methodAttribute?.ExampleResponse,
                        InvocationType = InvocationType.JsonRpc
                    };

                    methods.Add(methodName, methodData);
                }

                _modulesData.Add(rpcModuleName, methods);
            }
        }

        private void AddCliModulesData(List<Type> cliTypes)
        {
            foreach (Type cliType in cliTypes)
            {
                MethodInfo[] moduleMethods = cliType.GetMethods().ToArray();

                string cliModuleName = cliType.Name.Replace("CliModule", "").ToLower();

                if (_modulesData.Keys.Contains(cliModuleName))
                {
                    UpdateModule(cliModuleName, moduleMethods);
                }
                else
                {
                    AddNewModule(cliModuleName, moduleMethods);
                }
            }
        }

        private void AddNewModule(string cliModuleName, MethodInfo[] moduleMethods)
        {
            Dictionary<string, MethodData> methods =
                new Dictionary<string, MethodData>();

            foreach (MethodInfo moduleMethod in moduleMethods)
            {
                string methodName = GetCliMethodName(moduleMethod);
                AddNewMethod(methods, cliModuleName, methodName, moduleMethod);
            }

            _modulesData.Add(cliModuleName, methods);
        }

        private string GetCliMethodName(MethodInfo moduleMethod)
        {
            CliFunctionAttribute functionAttribute = moduleMethod.GetCustomAttribute<CliFunctionAttribute>();
            CliPropertyAttribute propertyAttribute = moduleMethod.GetCustomAttribute<CliPropertyAttribute>();

            return functionAttribute?.FunctionName ??
                   propertyAttribute?.PropertyName ??
                   string.Concat(moduleMethod.Name.Substring(0, 1).ToLower(), moduleMethod.Name.Substring(1));
        }

        private void UpdateModule(string cliModuleName, MethodInfo[] moduleMethods)
        {
            _modulesData.TryGetValue(cliModuleName, out var methods);

            foreach (MethodInfo moduleMethod in moduleMethods)
            {
                string methodName = GetCliMethodName(moduleMethod);

                if (methods?.Keys.Contains(methodName) ?? false)
                {
                    UpdateMethod(methods, methodName, moduleMethod);
                }
                else
                {
                    AddNewMethod(methods, cliModuleName, methodName, moduleMethod);
                }
            }
        }

        private void AddNewMethod(Dictionary<string, MethodData> methods, string cliModuleName, string methodName, MethodInfo moduleMethod)
        {
            CliFunctionAttribute functionAttribute = moduleMethod.GetCustomAttribute<CliFunctionAttribute>();
            CliPropertyAttribute propertyAttribute = moduleMethod.GetCustomAttribute<CliPropertyAttribute>();

            if (functionAttribute?.ObjectName == cliModuleName || propertyAttribute?.ObjectName == cliModuleName)
            {
                MethodData methodData = new MethodData()
                {
                    ReturnType = moduleMethod.ReturnType,
                    Parameters = moduleMethod.GetParameters(),
                    Description = functionAttribute?.Description ?? propertyAttribute?.Description,
                    ResponseDescription = functionAttribute?.ResponseDescription ?? propertyAttribute?.ResponseDescription,
                    ExampleResponse = functionAttribute?.ExampleResponse ?? propertyAttribute?.ExampleResponse,
                    IsFunction = functionAttribute != null,
                    InvocationType = InvocationType.Cli
                };

                methods.Add(methodName, methodData);
            }
        }

        private void UpdateMethod(Dictionary<string, MethodData> methods, string methodName, MethodInfo moduleMethod)
        {
            CliFunctionAttribute functionAttribute = moduleMethod.GetCustomAttribute<CliFunctionAttribute>();
            CliPropertyAttribute propertyAttribute = moduleMethod.GetCustomAttribute<CliPropertyAttribute>();

            methods.Remove(methodName, out MethodData commonMethod);
            commonMethod.InvocationType = InvocationType.Both;
            commonMethod.IsFunction = functionAttribute != null;

            if (commonMethod.Description?.Length == 0)
            {
                commonMethod.Description = functionAttribute?.Description ?? propertyAttribute?.Description;
            }

            if (commonMethod.ResponseDescription?.Length == 0)
            {
                commonMethod.ResponseDescription = functionAttribute?.ResponseDescription ?? propertyAttribute?.ResponseDescription;
            }

            if (commonMethod.ExampleResponse?.Length == 0)
            {
                commonMethod.ExampleResponse = functionAttribute?.ExampleResponse ?? propertyAttribute?.ExampleResponse;
            }

            methods.Add(methodName, commonMethod);
        }
    }
}
