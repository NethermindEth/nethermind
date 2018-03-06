namespace Nethermind.Mining
{
    public interface IEthashDataSet<out T>
    {
        uint Size { get; }
        T CalcDataSetItem(uint i);
    }
}