/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Nethermind.Cli.Modules;
using Nethermind.JsonRpc.Client;

namespace Nethermind.Cli
{
    public class CliModuleLoader
    {
        private readonly IJsonRpcClient _client;
        private readonly ICliEngine _engine;

        public List<string> ModuleNames { get; set; } = new List<string>();
        public Dictionary<string, List<string>> MethodsByModules { get; set; } = new Dictionary<string, List<string>>();
        
        public CliModuleLoader(ICliEngine engine, IJsonRpcClient client)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        /// <summary>
        /// Can just use Delegate.CreateDelegate???
        /// </summary>
        /// <param name="methodInfo"></param>
        /// <param name="module"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentNullException"></exception>
        private static Delegate CreateDelegate(MethodInfo methodInfo, CliModuleBase module)
        {
            if (methodInfo == null)
            {
                throw new ArgumentNullException(nameof(methodInfo));
            }

            ParameterInfo[] parameterInfos = methodInfo.GetParameters();
            Type[] types = new Type[parameterInfos.Length + 1];
            for (int i = 0; i < parameterInfos.Length; i++)
            {
                types[i] = parameterInfos[i].ParameterType;
            }

            types[parameterInfos.Length] = methodInfo.ReturnType;

            return methodInfo.CreateDelegate(Expression.GetDelegateType(types), module);
        }

        public void LoadModule(CliModuleBase module)
        {
            var cliModuleAttribute = module.GetType().GetCustomAttribute<CliModuleAttribute>();
            CliConsole.WriteLine($"module ({cliModuleAttribute.ModuleName})");
            ModuleNames.Add(cliModuleAttribute.ModuleName);
            MethodsByModules[cliModuleAttribute.ModuleName] = new List<string>();
            
            var methods = module.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (MethodInfo methodInfo in methods.OrderBy(m => m.Name))
            {
                var cliProperty = methodInfo.GetCustomAttribute<CliPropertyAttribute>();
                var cliFunction = methodInfo.GetCustomAttribute<CliFunctionAttribute>();

                bool isProperty = cliProperty != null;

                string objectName = cliProperty?.ObjectName ?? cliFunction?.ObjectName;
                string itemName = cliProperty?.PropertyName ?? cliFunction?.FunctionName;

                if (objectName == null)
                {
                    throw new InvalidDataException($"Method {methodInfo.Name} of {module.GetType().Name} should be decorated with one of {nameof(CliPropertyAttribute)} or {nameof(CliFunctionAttribute)}");
                }

                ObjectInstance instance;
                if (!_objects.ContainsKey(objectName))
                {
                    instance = _engine.JintEngine.Object.Construct(Arguments.Empty);
                    _engine.JintEngine.SetValue(objectName, instance);
                    _objects[objectName] = instance;
                }

                instance = _objects[objectName];
                var @delegate = CreateDelegate(methodInfo, module);
                DelegateWrapper nativeDelegate = new DelegateWrapper(_engine.JintEngine, @delegate);

                if (isProperty)
                {
                    CliConsole.WriteKeyword($"  {objectName}");
                    CliConsole.WriteLine($".{itemName}");
                    
                    MethodsByModules[objectName].Add(itemName);
                    AddProperty(instance, itemName, nativeDelegate);
                }
                else
                {
                    CliConsole.WriteKeyword($"  {objectName}");
                    CliConsole.WriteLine($".{itemName}({string.Join(", ", methodInfo.GetParameters().Select(p => p.Name))})");

                    MethodsByModules[objectName].Add(itemName + "(");
                    AddMethod(instance, itemName, nativeDelegate);
                }
            }
            
            CliConsole.WriteLine();
        }
        
        public void LoadModule(Type type)
        {   
            var ctor = type.GetConstructor(new[] {typeof(ICliEngine), typeof(INodeManager)});
            CliModuleBase module = (CliModuleBase) ctor.Invoke(new object[] {_engine, _client});
            LoadModule(module);
        }

        private Dictionary<string, ObjectInstance> _objects = new Dictionary<string, ObjectInstance>();

        private void AddMethod(ObjectInstance instance, string name, DelegateWrapper delegateWrapper)
        {   
            instance.FastAddProperty(name, delegateWrapper, true, false, true);
        }

        private void AddProperty(ObjectInstance instance, string name, DelegateWrapper delegateWrapper)
        {
            JsValue getter = JsValue.FromObject(_engine.JintEngine, delegateWrapper);
            JsValue setter = JsValue.Null;

            instance.DefineOwnProperty(name, new PropertyDescriptor(getter, setter, true, false), true);
        }
    }
}