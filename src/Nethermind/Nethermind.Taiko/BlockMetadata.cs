using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using System.Text.Json.Serialization;
using Nethermind.JsonRpc.Converters;

namespace Nethermind.Taiko;

public class BlockMetadata
{
    // Fields defined in `LibData.blockMetadata`.
    public Address? Beneficiary { get; set; } // common.Address `json:"beneficiary"  gencodec:"required"`
    public long GasLimit { get; set; }        // uint64 `json:"gasLimit"     gencodec:"required"`
    public ulong Timestamp { get; set; }      // uint64         `json:"timestamp"    gencodec:"required"`
    public Hash256? MixHash { get; set; }     // common.Hash    `json:"mixHash"      gencodec:"required"`

    // Extra fields required in taiko-geth.
    public byte[]? TxList { get; set; }         // []byte   `json:"txList"          gencodec:"required"`
    public UInt256 HighestBlockID { get; set; } // *big.Int `json:"highestBlockID"  gencodec:"required"`

    [JsonConverter(typeof(Base64Converter))]
    public byte[]? ExtraData { get; set; }      // []byte   `json:"extraData"       gencodec:"required"`
}
