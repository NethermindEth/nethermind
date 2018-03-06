namespace Nethermind.Mining
{
    public interface IEthashDataSet
    {
        uint Size { get; }
        uint[] CalcDataSetItem(uint i);
    }
}