using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Nevermind.JsonRpc.DataModel;

namespace Nevermind.JsonRpc.Module
{
    public class ModuleInfo
    {
        public ModuleInfo(ModuleType moduleType, Type moduleInterface, object moduleObject)
        {
            ModuleInterface = moduleInterface;
            ModuleObject = moduleObject;
            ModuleType = moduleType;
            Initialize();
        }

        public ModuleType ModuleType { get; }
        public Type ModuleInterface { get; }
        public object ModuleObject { get; }
        public IDictionary<string, MethodInfo> MethodDictionary { get; private set; }

        private void Initialize()
        {
            MethodDictionary = GetMethodDict(ModuleInterface);
        }

        private IDictionary<string, MethodInfo> GetMethodDict(Type type)
        {
            var methods = type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly);
            return methods.ToDictionary(x => x.Name.Trim().ToLower());
        }
    }
}