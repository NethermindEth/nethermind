namespace Nethermind.DataMarketplace.Providers.Domain
{
    public class GasLimits
    {
        public ulong Payment { get; }

        public GasLimits(ulong payment)
        {
            Payment = payment;
        }
    }
}