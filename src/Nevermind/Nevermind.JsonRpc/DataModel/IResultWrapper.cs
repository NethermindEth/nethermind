using Nevermind.Utils.Model;

namespace Nevermind.JsonRpc.DataModel
{
    public interface IResultWrapper
    {
        Result GetResult();
        object GetData();
    }
}