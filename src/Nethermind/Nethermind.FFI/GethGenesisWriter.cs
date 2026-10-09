// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Specs.ChainSpecStyle.Json;

namespace Nethermind.FFI;

/// <summary>Writes a chain spec as an EIP-7949 Geth-style genesis.json, without the allocations.</summary>
/// <remarks>
/// Config values are plain JSON numbers as in Geth's own files: alloy-genesis parses <c>chainId</c> and the blob
/// schedule only from numbers. Header values are hex quantities.
/// </remarks>
public static class GethGenesisWriter
{
    public static byte[] Write(ChainSpec chainSpec, BlockHeader genesis)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer))
        {
            writer.WriteStartObject();
            WriteConfig(writer, chainSpec);
            writer.WriteString("nonce", genesis.Nonce.ToHexString(true));
            writer.WriteString("timestamp", genesis.Timestamp.ToHexString(true));
            writer.WriteString("extraData", genesis.ExtraData.ToHexString(true));
            writer.WriteString("gasLimit", genesis.GasLimit.ToHexString(true));
            writer.WriteString("difficulty", genesis.Difficulty.ToHexString(true));
            writer.WriteString("mixHash", (genesis.MixHash ?? Keccak.Zero).ToString());
            writer.WriteString("coinbase", (genesis.Beneficiary ?? Address.Zero).ToString());
            if (!genesis.BaseFeePerGas.IsZero) writer.WriteString("baseFeePerGas", genesis.BaseFeePerGas.ToHexString(true));
            if (genesis.ExcessBlobGas is { } excessBlobGas) writer.WriteString("excessBlobGas", excessBlobGas.ToHexString(true));
            if (genesis.BlobGasUsed is { } blobGasUsed) writer.WriteString("blobGasUsed", blobGasUsed.ToHexString(true));
            writer.WriteEndObject();
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteConfig(Utf8JsonWriter writer, ChainSpec chainSpec)
    {
        writer.WriteStartObject("config");
        writer.WriteNumber("chainId", chainSpec.ChainId);
        WriteActivation(writer, "homesteadBlock", chainSpec.HomesteadBlockNumber);
        if (chainSpec.DaoForkBlockNumber is not null)
        {
            WriteActivation(writer, "daoForkBlock", chainSpec.DaoForkBlockNumber);
            writer.WriteBoolean("daoForkSupport", true);
        }

        WriteActivation(writer, "eip150Block", chainSpec.TangerineWhistleBlockNumber);
        WriteActivation(writer, "eip155Block", chainSpec.SpuriousDragonBlockNumber);
        WriteActivation(writer, "eip158Block", chainSpec.SpuriousDragonBlockNumber);
        WriteActivation(writer, "byzantiumBlock", chainSpec.ByzantiumBlockNumber);
        // Parity-style chain specs leave Constantinople unset when it activated together with Petersburg.
        WriteActivation(writer, "constantinopleBlock", chainSpec.ConstantinopleBlockNumber ?? chainSpec.ConstantinopleFixBlockNumber);
        WriteActivation(writer, "petersburgBlock", chainSpec.ConstantinopleFixBlockNumber);
        WriteActivation(writer, "istanbulBlock", chainSpec.IstanbulBlockNumber);
        WriteActivation(writer, "muirGlacierBlock", chainSpec.MuirGlacierNumber);
        WriteActivation(writer, "berlinBlock", chainSpec.BerlinBlockNumber);
        WriteActivation(writer, "londonBlock", chainSpec.LondonBlockNumber);
        WriteActivation(writer, "arrowGlacierBlock", chainSpec.ArrowGlacierBlockNumber);
        WriteActivation(writer, "grayGlacierBlock", chainSpec.GrayGlacierBlockNumber);
        WriteActivation(writer, "mergeNetsplitBlock", chainSpec.MergeForkIdBlockNumber);

        (string Name, ulong? Timestamp)[] timestampForks =
        [
            ("cancun", chainSpec.CancunTimestamp),
            ("prague", chainSpec.PragueTimestamp),
            ("osaka", chainSpec.OsakaTimestamp),
            ("amsterdam", chainSpec.AmsterdamTimestamp),
        ];
        WriteActivation(writer, "shanghaiTime", chainSpec.ShanghaiTimestamp);
        foreach ((string name, ulong? timestamp) in timestampForks) WriteActivation(writer, $"{name}Time", timestamp);

        // A blob schedule entry belongs to the latest named fork activating at its timestamp; any other entry is the
        // next blob-parameter-only (BPO) fork.
        List<(string Name, BlobScheduleSettings Settings)> blobSchedule = [];
        int bpoCount = 0;
        foreach (BlobScheduleSettings settings in chainSpec.Parameters.BlobSchedule ?? [])
        {
            string? fork = timestampForks.LastOrDefault(f => f.Timestamp == settings.Timestamp).Name;
            if (fork is null)
            {
                fork = $"bpo{++bpoCount}";
                WriteActivation(writer, $"{fork}Time", settings.Timestamp);
            }

            blobSchedule.Add((fork, settings));
        }

        if (chainSpec.TerminalTotalDifficulty is { } terminalTotalDifficulty)
        {
            writer.WritePropertyName("terminalTotalDifficulty");
            writer.WriteRawValue(terminalTotalDifficulty.ToString());
        }

        if (chainSpec.Parameters.DepositContractAddress is { } depositContract)
            writer.WriteString("depositContractAddress", depositContract.ToString());

        writer.WriteStartObject("blobSchedule");
        foreach ((string name, BlobScheduleSettings settings) in blobSchedule)
        {
            writer.WriteStartObject(name);
            writer.WriteNumber("target", settings.Target);
            writer.WriteNumber("max", settings.Max);
            writer.WriteNumber("baseFeeUpdateFraction", settings.BaseFeeUpdateFraction);
            writer.WriteEndObject();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteActivation(Utf8JsonWriter writer, string name, ulong? activation)
    {
        if (activation is { } value) writer.WriteNumber(name, value);
    }
}
