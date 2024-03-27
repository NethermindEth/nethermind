using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.Serialization.Json;

namespace Evm.T8NTool;

using System.Text.Json;
using System.Text.Json.Serialization;
using Nethermind.Core;

public class ReceiptJsonConverter : JsonConverter<TxReceipt>
{
    private readonly TxTypeConverter _txTypeConverter = new();
    private readonly Hash256Converter _hash256Converter = new();
    private readonly UInt256Converter _uInt256Converter = new();
    
    private readonly LongConverter _longConverter = new();
    private readonly BloomConverter _bloomConverter = new();
    private readonly EthereumJsonSerializer _ethereumJsonSerializer = new();
    private readonly AddressConverter _addressConverter = new();
    private readonly IntConverter _intConverter = new();
    private readonly ByteReadOnlyMemoryConverter _byteConverter = new();
    private readonly MemoryByteConverter _memoryByteConverter = new();

    public override TxReceipt Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotImplementedException();
    }

    public override void Write(Utf8JsonWriter writer, TxReceipt receipt, JsonSerializerOptions options)
    {
        writer.WriteStartObject();

        if (receipt.TxType != TxType.Legacy)
        {
            writer.WritePropertyName("type");
            _txTypeConverter.Write(writer, receipt.TxType, options);
        }
        writer.WritePropertyName("root");
        ByteArrayConverter.Convert(writer, receipt.PostTransactionState != null ? receipt.PostTransactionState.Bytes : Bytes.ZeroByte.ToArray());
        var status = receipt.StatusCode;
        writer.WritePropertyName("status");
        if (status == 0)
        {
            writer.WriteRawValue("\"0x0\""u8, skipInputValidation: true);
        }
        else
        {
            ByteArrayConverter.Convert(writer, MemoryMarshal.CreateReadOnlySpan(ref status, 1));
        }

        writer.WritePropertyName("cumulativeGasUsed");
        _longConverter.Write(writer, receipt.GasUsedTotal, options);
        writer.WritePropertyName("logsBloom");
        _bloomConverter.Write(writer, receipt.Bloom, options);
        writer.WriteNull("logs");
        writer.WritePropertyName("transactionHash");
        _hash256Converter.Write(writer, receipt.TxHash, options);
        writer.WritePropertyName("contractAddress");
        _addressConverter.Write(writer, receipt.ContractAddress ?? Address.Zero, options);
        writer.WritePropertyName("gasUsed");
        _longConverter.Write(writer, receipt.GasUsed, options);
        writer.WriteNull("effectiveGasPrice");
        writer.WritePropertyName("blockHash");
        _hash256Converter.Write(writer, receipt.BlockHash ?? Keccak.Zero, options);

        writer.WritePropertyName("transactionIndex");
        _uInt256Converter.Write(writer, UInt256.Parse(receipt.Index.ToString(), NumberStyles.Integer), options);

        writer.WriteEndObject();
    }
}
