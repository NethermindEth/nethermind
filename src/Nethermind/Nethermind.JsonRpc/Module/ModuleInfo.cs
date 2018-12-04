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
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace Nethermind.JsonRpc.Module
{
    public class ModuleInfo
    {
        public ModuleInfo(ModuleType moduleType, Type moduleInterface, IModule moduleObject)
        {
            ModuleInterface = moduleInterface;
            ModuleObject = moduleObject;
            ModuleType = moduleType;
            MethodDictionary = GetMethodDict(ModuleInterface);
            Converters = moduleObject.GetConverters();
        }

        public ModuleType ModuleType { get; }
        public Type ModuleInterface { get; }
        public object ModuleObject { get; }
        public IDictionary<string, MethodInfo> MethodDictionary { get;  }
        public IReadOnlyCollection<JsonConverter> Converters { get; }

        private IDictionary<string, MethodInfo> GetMethodDict(Type type)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            return methods.ToDictionary(x => x.Name.Trim().ToLower());
        }
    }
}