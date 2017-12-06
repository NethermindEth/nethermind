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
            return !IsSynching ? "false" : JsonConvert.SerializeObject(new { startingBlock = StartingBlock.ToString(), currentBlock = CurrentBlock.ToString(), highestBlock = HighestBlock.ToString() });
        }
    }
}