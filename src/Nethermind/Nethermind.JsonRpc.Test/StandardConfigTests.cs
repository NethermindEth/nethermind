// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Nethermind.JsonRpc.Modules;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test
{
    public static class StandardJsonRpcTests
    {
        public static void ValidateDocumentation()
        {
            ForEachMethod(CheckDescribed);
        }

        private static void ForEachMethod(Action<MethodInfo> verifier)
        {
            string[] dlls = Directory.GetFiles(AppDomain.CurrentDomain.BaseDirectory, "Nethermind.*.dll")
                .OrderBy(n => n).ToArray();
            foreach (string dll in dlls)
            {
                TestContext.WriteLine($"Verifying {nameof(StandardJsonRpcTests)} on {Path.GetFileName(dll)}");
                Assembly assembly = Assembly.LoadFile(dll);
                Type[] modules = assembly.GetExportedTypes().Where(FilterTypes).ToArray();

                CheckModules(verifier, modules);
            }

            // needed because otherwise wrong types are resolved
            CheckModules(verifier, typeof(IRpcModule).Assembly.GetExportedTypes().Where(FilterTypes).ToArray());
        }

        private static bool FilterTypes(Type t)
        {
            return typeof(IRpcModule).IsAssignableFrom(t) && t.IsInterface && t != typeof(IContextAwareRpcModule);
        }

        private static void CheckModules(Action<MethodInfo> verifier, Type[] modules)
        {
            foreach (Type jsonRpcType in modules)
            {
                MethodInfo[] methodInfos = jsonRpcType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
                foreach (MethodInfo methodInfo in methodInfos)
                {
                    try
                    {
                        verifier(methodInfo);
                    }
                    catch (Exception e)
                    {
                        throw new Exception($"{jsonRpcType.Name}.{methodInfo.Name}", e);
                    }
                }
            }
        }

        private static void CheckDescribed(MethodInfo method)
        {
            JsonRpcMethodAttribute? attribute = method.GetCustomAttribute<JsonRpcMethodAttribute>();
            // this should really check if the string is not empty
            if (attribute?.Description is null)
            {
                throw new AssertionException($"JSON RPC method {method.DeclaringType}.{method.Name} has no description.");
            }
        }
    }
}
