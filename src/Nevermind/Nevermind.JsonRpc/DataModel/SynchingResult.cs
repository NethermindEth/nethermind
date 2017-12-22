using Nevermind.Json;
using Newtonsoft.Json;

namespace Nevermind.JsonRpc.DataModel
{
    public class SynchingResult : IJsonRpcResult
    {
        public bool IsSynching { get; set; }
        public Quantity StartingBlock { get; set; }
        public Quantity CurrentBlock { get; set; }
        public Quantity HighestBlock { get; set; }

        public object ToJson()
        {
            return !IsSynching ? "false" : (object)new { startingBlock = StartingBlock.ToJson(), currentBlock = CurrentBlock.ToJson(), highestBlock = HighestBlock.ToJson() };
        }
    }
}