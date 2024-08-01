namespace Ethereum.Test.Base.Interfaces
{
    public interface IEthereumTest
    {
        string? Category { get; set; }
        string? Name { get; set; }
        string? LoadFailure { get; set; }
    }
}
