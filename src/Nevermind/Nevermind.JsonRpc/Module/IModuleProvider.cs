using System.Collections.Generic;

namespace Nevermind.JsonRpc.Module
{
    public interface IModuleProvider
    {
        IEnumerable<ModuleInfo> GetEnabledModules();
        IEnumerable<ModuleInfo> GetAllModules();
    }
}