namespace Nethermind.AccountAbstraction.Bundler
{
    public interface IGasLimitProvider
    {
        ulong GetGasLimit();
    }
}
