namespace Nethermind.Mining
{
    public interface IEthashDataSet<out T>
    {
        uint Size { get; }
        uint[] CalcDataSetItem(uint i);
    }
}