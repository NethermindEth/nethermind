using System;

namespace Nevermind.JsonRpc.DataModel
{
    public class BlockParameter : IJsonRpcRequest
    {
        public BlockParameterType Type { get; set; }
        public Quantity BlockId { get; set; }

        public void FromJson(string jsonValue)
        {
            if (string.IsNullOrEmpty(jsonValue))
            {
                throw new Exception("Empty parameter");
            }
            if (Enum.TryParse(jsonValue, true, out BlockParameterType type))
            {
                Type = type;
                return;
            }
            Type = BlockParameterType.BlockId;
            BlockId = new Quantity();
            BlockId.FromJson(jsonValue);
        }
    }
}