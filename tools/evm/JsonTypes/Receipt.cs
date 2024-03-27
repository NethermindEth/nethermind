using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Evm.JsonTypes;

public class Receipt
{
    public TxType Type;
    public Hash256 Root;
    public byte Status;
    public long CumulativeGasUsed;
    public Hash256 LogsBloom;
    public LogEntry[] Logs;
    public Hash256 TransactionHash;
    public Address ContractAddress;
    public long GasUsed;
    public UInt256 EffectiveGasPrice;
    public UInt256 BlobGasUsed;
    public UInt256 BlobGasPrice;
}