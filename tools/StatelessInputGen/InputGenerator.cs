// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Globalization;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Stateless.Execution;
using Spectre.Console;

namespace Nethermind.StatelessInputGen;

internal static class InputGenerator
{
    internal static async Task<int> Generate(string blockParam, Uri host, string output, bool forZisk)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockParam);
        ArgumentNullException.ThrowIfNull(host);

        byte[] data;
        Witness? witness;

        (Block? block, witness, ulong? chainId) = await FetchData(blockParam, host);

        using (witness)
        {
            if (block is null || witness is null || chainId is null)
                return 1;

            data = InputSerializer.Serialize(block, witness, chainId.Value);
        }

        if (forZisk)
        {
            int rem = data.Length % sizeof(ulong);
            int len = sizeof(ulong) + data.Length + (rem == 0 ? 0 : (sizeof(ulong) - rem));
            byte[] framedData = new byte[len];

            BinaryPrimitives.WriteUInt64LittleEndian(framedData, (ulong)data.Length);
            Buffer.BlockCopy(data, 0, framedData, sizeof(ulong), data.Length);

            data = framedData;
        }

        Directory.CreateDirectory(output);

        string fileName = $"{EnsureBlockParamIsNumber(blockParam, block)}.bin";
        string path = Path.Join(output, fileName);

        File.WriteAllBytes(path, data);

        AnsiConsole.MarkupLine($"[green]✓[/] Saved to [dim]{Path.GetDirectoryName(path)}{Path.DirectorySeparatorChar}[/]{fileName}");

        return 0;
    }

    private static async Task<(Block?, Witness?, ulong? chainId)> FetchData(string blockParam, Uri host)
    {
        EthereumJsonSerializer serializer = new([new OwnedReadOnlyListConverter()]);
        using BasicJsonRpcClient client = new(host, serializer, NullLogManager.Instance);
        Block? block = null;
        Witness? witness = null;
        ulong? chainId = null;

        await AnsiConsole
            .Status()
            .Spinner(Spinner.Known.Default)
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync($"[orange1]Fetching block `{blockParam}`[/]", async ctx =>
            {
                string? rlpHex = await client.Post<string>("debug_getRawBlock", EnsureIsHexIfNumber(blockParam));

                if (string.IsNullOrEmpty(rlpHex))
                {
                    AnsiConsole.MarkupLine($"[red]Block not found[/]");
                    return;
                }

                byte[] rlp = Convert.FromHexString(rlpHex![2..]);

                IRlpValueDecoder<Block> blockDecoder = Rlp.GetValueDecoder<Block>()!;
                Rlp.ValueDecoderContext blockContext = new(rlp);
                block = blockDecoder.Decode(ref blockContext, RlpBehaviors.None);
                blockContext.Check(rlp.Length);

                string blockNumber = EnsureBlockParamIsNumber(blockParam, block);

                AnsiConsole.MarkupLine($"[green]✓[/] Fetched block {blockNumber}: {rlp.Length:N0} bytes");

                ctx.Status = $"[orange1]Fetching witness for block {blockNumber}[/]";

                witness = await client.Post<Witness>("debug_executionWitness", $"0x{block.Number:x}");

                if (witness is null)
                {
                    AnsiConsole.MarkupLine($"[red]Witness not found[/]");
                    return;
                }

                AnsiConsole.MarkupLine(
                    $"[green]✓[/] Fetched witness for block {blockNumber}: {GetWitnessSize(witness):N0} bytes");

                ctx.Status = $"[orange1]Fetching chain id[/]";

                chainId = await client.Post<ulong?>("eth_chainId");

                if (chainId is null)
                {
                    AnsiConsole.MarkupLine($"[red]Chain not found[/]");
                    return;
                }

                AnsiConsole.MarkupLine($"[green]✓[/] Chain: {GetChainName(chainId.Value)}");
            });

        return (block, witness, chainId);
    }

    /// <summary>
    /// If the block parameter is a number, ensures that the same format provided by the user is used.
    /// Otherwise, uses the <c>block</c>'s number.
    /// </summary>
    private static string EnsureBlockParamIsNumber(string blockParameter, Block block) =>
        blockParameter.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
        ulong.TryParse(blockParameter[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _) ||
        ulong.TryParse(blockParameter, NumberStyles.None, CultureInfo.InvariantCulture, out _)
            ? blockParameter
            : block.Number.ToString();

    private static string EnsureIsHexIfNumber(string blockParameter)
    {
        if (ulong.TryParse(blockParameter, NumberStyles.None, CultureInfo.InvariantCulture, out ulong number))
            return $"0x{number:x}";

        return blockParameter;
    }

    private static int GetWitnessSize(Witness witness)
    {
        var size = 0;

        foreach (var code in witness.Codes)
            size += code.Length;

        foreach (var header in witness.Headers)
            size += header.Length;

        foreach (var key in witness.Keys)
            size += key.Length;

        foreach (var state in witness.State)
            size += state.Length;

        return size;
    }

    private static string GetChainName(ulong chainId) => chainId switch
    {
        BlockchainIds.Hoodi => "Hoodi",
        BlockchainIds.Mainnet => "Mainnet",
        BlockchainIds.Sepolia => "Sepolia",
        _ => $"Not supported ({chainId})"
    };
}
