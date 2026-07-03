// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Globalization;
using Nethermind.Consensus.Stateless;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stateless.Execution.IO;
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

            StatelessInput<SszExecutionPayloadV3> input = new()
            {
                NewPayloadRequest = NewPayloadRequest<SszExecutionPayloadV3>.From(block),
                Witness = ExecutionWitness.From(witness),
                ChainConfig = new()
                {
                    ChainId = chainId.Value,
                    ActiveFork = ForkConfig.From(block.Header, GetSpecProvider(chainId.Value))
                },
                PublicKeys = RecoverPublicKeys(block.Transactions, chainId.Value)
            };

            byte[] encoded = StatelessInput<SszExecutionPayloadV3>.Encode(input);
            data = new byte[encoded.Length + sizeof(ushort)];

            BinaryPrimitives.WriteUInt16BigEndian(data, 0);

            Buffer.BlockCopy(encoded, 0, data, sizeof(ushort), encoded.Length);
        }

        if (forZisk)
        {
            data = ZiskFrame.Wrap(data);
        }

        Directory.CreateDirectory(output);

        string fileName = $"{EnsureBlockParamIsNumber(blockParam, block)}.ssz";
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

                IRlpDecoder<Block> blockDecoder = Rlp.GetDecoder<Block>()!;
                RlpReader blockContext = new(rlp);
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

                ctx.Status = $"[orange1]Fetching chainId id[/]";

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
        int size = 0;

        foreach (byte[] code in witness.Codes)
            size += code.Length;

        foreach (byte[] header in witness.Headers)
            size += header.Length;

        foreach (byte[] key in witness.Keys)
            size += key.Length;

        foreach (byte[] state in witness.State)
            size += state.Length;

        return size;
    }

    private static string GetChainName(ulong chainId) => chainId switch
    {
        BlockchainIds.Hoodi => "Hoodi",
        BlockchainIds.Mainnet => "Mainnet",
        BlockchainIds.Sepolia => "Sepolia",
        _ => $"Unknown ({chainId})"
    };

    internal static ISpecProvider GetSpecProvider(ulong chainId) =>
        ChainSpecBasedSpecProvider.KnownProvidersByChainId.TryGetValue(chainId, out IForkAwareSpecProvider? specProvider)
            ? specProvider
            : throw new ArgumentException($"Unknown chain id: {chainId}", nameof(chainId));

    private static SszPublicKeys[] RecoverPublicKeys(ReadOnlySpan<Transaction> transactions, ulong chainId)
    {
        EthereumEcdsa ecdsa = new(chainId);
        SszPublicKeys[] publicKeys = new SszPublicKeys[transactions.Length];

        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction tx = transactions[i];
            PublicKey publicKey = ecdsa.RecoverPublicKey(tx)
                ?? throw new InvalidOperationException($"Failed to recover public key for transaction {tx.Hash}");

            publicKeys[i] = new()
            {
                Bytes = publicKey.PrefixedBytes
            };
        }

        return publicKeys;
    }
}
