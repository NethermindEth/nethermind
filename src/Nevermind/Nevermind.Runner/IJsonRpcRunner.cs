using System.Collections.Generic;
using Nevermind.JsonRpc.DataModel;
using Unity;

namespace Nevermind.Runner
{
    public interface IJsonRpcRunner
    {
        void Start(IEnumerable<ModuleType> modules = null);
        void Stop(IEnumerable<ModuleType> modules = null);
        IUnityContainer Container { set; }
    }
}