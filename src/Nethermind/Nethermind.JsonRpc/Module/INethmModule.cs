namespace Nethermind.JsonRpc.Module
{
    using System.Collections.Generic;
    using Nethermind.JsonRpc.DataModel;
    
    public interface INethmModule : IModule
    {
        ResultWrapper<IEnumerable<string>> nethm_getCompilers();
        ResultWrapper<Data> nethm_compileLLL(string code);
        ResultWrapper<string> nethm_compileSolidity(string parameters);
        ResultWrapper<Data> nethm_compileSerpent(string code);
    }
}