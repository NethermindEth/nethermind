using System.Collections.Generic;

namespace Nevermind.JsonRpc.Module
{
    public interface IModuleProvider
    {
        IReadOnlyCollection<ModuleInfo> GetEnabledModules();
        IReadOnlyCollection<ModuleInfo> GetAllModules();
    }
}