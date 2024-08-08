namespace Nethermind.Core;

public sealed class TxFeature
{
    public static readonly TxFeature AccessList = new();
    public static readonly TxFeature EIP1559 = new();
    public static readonly TxFeature Blob = new();
}
