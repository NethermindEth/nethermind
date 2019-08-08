namespace Nethermind.Overseer.Test.JsonRpc.Dto
{
    public class MakeDepositDto
    {
        public string DataAssetId { get; set; }
        public uint Units { get; set; }
        public string Value { get; set; }
        public uint ExpiryTime { get; set; }
    }
}