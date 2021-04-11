//  Copyright (c) 2021 Demerzel Solutions Limited
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
// 

using System;
using System.IO;
using System.Linq;
using System.Reflection;
using Jint.Parser.Ast;
using Microsoft.FSharp.Reflection;
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
                TestContext.WriteLine($"  Verifying JSON RPC type {jsonRpcType.Name}");
                MethodInfo[] methodInfos = jsonRpcType.GetMethods(BindingFlags.Instance | BindingFlags.Public);
                foreach (MethodInfo methodInfo in methodInfos)
                {
                    try
                    {
                        TestContext.WriteLine($"    Verifying JSON RPC property {methodInfo.Name}");
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
            JsonRpcMethodAttribute attribute = method.GetCustomAttribute<JsonRpcMethodAttribute>();
            // this should really check if the string is not empty
            if (attribute?.Description is null)
            {
                throw new AssertionException(
                    $"JSON RPC method {method.DeclaringType}.{method.Name} has no description.");
            }
        }
    }
}
