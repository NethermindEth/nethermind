namespace Nethermind.DataMarketplace.Integration.Test.JsonRpc.Dto
{
    public class MakeDepositDto
    {
        public string DataHeaderId { get; set; }
        public uint Units { get; set; }
        public string Value { get; set; }
    }
}