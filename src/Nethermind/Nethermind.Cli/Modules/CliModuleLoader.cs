// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.Loader;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Descriptors;
using Jint.Runtime.Interop;
using Nethermind.Cli.Console;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;

namespace Nethermind.Cli.Modules
{
    public class CliModuleLoader
    {
        private readonly IJsonRpcClient _client;
        private readonly ICliConsole _cliConsole;
        private readonly ICliEngine _engine;

        public List<string> ModuleNames { get; } = new List<string>();

        public Dictionary<string, List<string>> MethodsByModules { get; } = new Dictionary<string, List<string>>();

        public CliModuleLoader(ICliEngine engine, IJsonRpcClient client, ICliConsole cliConsole)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _cliConsole = cliConsole ?? throw new ArgumentNullException(nameof(cliConsole));
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
            if (methodInfo is null)
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

        private void LoadModule(CliModuleBase module)
        {
            CliModuleAttribute? cliModuleAttribute = module.GetType().GetCustomAttribute<CliModuleAttribute>();
            if (cliModuleAttribute is null)
            {
                _cliConsole.WriteErrorLine(
                    $"Could not load module {module.GetType().Name} bacause of a missing {nameof(CliModuleAttribute)}.");
                return;
            }

            _cliConsole.WriteLine($"module ({cliModuleAttribute.ModuleName})");
            ModuleNames.Add(cliModuleAttribute.ModuleName);
            MethodsByModules[cliModuleAttribute.ModuleName] = new List<string>();

            var methods = module.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            foreach (MethodInfo methodInfo in methods.OrderBy(m => m.Name))
            {
                var cliProperty = methodInfo.GetCustomAttribute<CliPropertyAttribute>();
                var cliFunction = methodInfo.GetCustomAttribute<CliFunctionAttribute>();

                bool isProperty = cliProperty is not null;

                string? objectName = cliProperty?.ObjectName ?? cliFunction?.ObjectName;
                string? itemName = cliProperty?.PropertyName ?? cliFunction?.FunctionName;

                if (objectName is null)
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

                if (itemName is not null)
                {
                    if (isProperty)
                    {
                        _cliConsole.WriteKeyword($"  {objectName}");
                        _cliConsole.WriteLine($".{itemName}");

                        MethodsByModules[objectName].Add(itemName);
                        AddProperty(instance, itemName, nativeDelegate);
                    }
                    else
                    {
                        _cliConsole.WriteKeyword($"  {objectName}");
                        _cliConsole.WriteLine($".{itemName}({string.Join(", ", methodInfo.GetParameters().Select(p => p.Name))})");

                        MethodsByModules[objectName].Add(itemName + "(");
                        AddMethod(instance, itemName, nativeDelegate);
                    }
                }
            }

            _cliConsole.WriteLine();
        }

        public void DiscoverAndLoadModules()
        {
            List<Type> moduleTypes = new List<Type>();

            string baseDir = string.Empty.GetApplicationResourcePath();
            string pluginsDir = "plugins".GetApplicationResourcePath();

            string searchPattern = "Nethermind*.dll";
            string[] allDlls =
                Directory.GetFiles(baseDir, searchPattern);
            if (Directory.Exists(pluginsDir))
            {
                allDlls = allDlls
                    .Union(Directory.GetFiles(pluginsDir, searchPattern)).ToArray();
            }

            AssemblyLoadContext.Default.Resolving += (context, name) =>
            {
                string fileName = name.Name + ".dll";
                try
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(baseDir, fileName));
                }
                catch (FileNotFoundException)
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(pluginsDir, fileName));
                }
            };

            moduleTypes.AddRange(GetType().Assembly.GetExportedTypes().Where(IsCliModule));
            foreach (string dll in allDlls)
            {
                Assembly assembly;
                try
                {
                    assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(dll);
                }
                catch (Exception)
                {
                    continue;
                }

                if (assembly != GetType().Assembly)
                {
                    moduleTypes.AddRange(assembly.GetExportedTypes().Where(IsCliModule));
                }
            }

            foreach (Type moduleType in moduleTypes.OrderBy(mt => mt.Name))
            {
                LoadModule(moduleType);
            }
        }

        private bool IsCliModule(Type type)
        {
            bool isCliModule = !type.IsAbstract && typeof(CliModuleBase).IsAssignableFrom(type);
            bool hasAttribute = type.GetCustomAttribute<CliModuleAttribute>() is not null;
            if (isCliModule && !hasAttribute)
            {
                _cliConsole.WriteInteresting(
                    $"Type {type.Name} is a CLI module but is not marked with a {nameof(CliModuleAttribute)}");
            }

            if (!isCliModule && hasAttribute)
            {
                _cliConsole.WriteInteresting(
                    $"Type {type.Name} is marked with a {nameof(CliModuleAttribute)} but does not extend {nameof(CliModuleBase)}");
            }

            return hasAttribute;
        }

        // ReSharper disable once MemberCanBePrivate.Global
        public void LoadModule(Type type)
        {
            ConstructorInfo? ctor = type.GetConstructor(new[] { typeof(ICliEngine), typeof(INodeManager) });
            if (ctor is not null)
            {
                CliModuleBase module = (CliModuleBase)ctor.Invoke(new object[] { _engine, _client });
                LoadModule(module);
            }
            else
            {
                _cliConsole.WriteErrorLine(
                    $"Could not load module {type.Name} because of a missing module constructor");
            }
        }

        private Dictionary<string, ObjectInstance> _objects = new Dictionary<string, ObjectInstance>();

        private static void AddMethod(ObjectInstance instance, string name, DelegateWrapper delegateWrapper)
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
