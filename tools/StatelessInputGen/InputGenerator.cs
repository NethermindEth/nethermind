// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers.Binary;
using System.Globalization;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.ExecutionRequests;
using Nethermind.Consensus.Processing;
using Nethermind.Consensus.Stateless;
using Nethermind.Consensus.Validators;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.ExecutionRequest;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Ssz;
using Nethermind.Specs.ChainSpecStyle;
using Nethermind.Stateless.Execution.IO;
using Spectre.Console;

namespace Nethermind.StatelessInputGen;

internal static class InputGenerator
{
    internal static async Task<int> Generate(string blockParam, Uri host, string output, bool forZisk, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(blockParam);
        ArgumentNullException.ThrowIfNull(host);

        byte[]? data;
        Witness? witness;

        (Block? block, witness, ulong? chainId) = await FetchData(blockParam, host, cancellationToken);
        if (block is null || witness is null || chainId is null)
            return 1;

        using (witness)
        {
            ISpecProvider specProvider = GetSpecProvider(chainId.Value);
            data = await EncodeInput(block, witness, specProvider, cancellationToken);
        }

        if (data is null)
            return 1;

        if (forZisk)
            data = ZiskFrame.Wrap(data);

        Directory.CreateDirectory(output);

        string fileName = $"{EnsureBlockParamIsNumber(blockParam, block)}.ssz";
        string path = Path.Join(output, fileName);

        await File.WriteAllBytesAsync(path, data, cancellationToken);

        AnsiConsole.MarkupLine($"[green]✓[/] Saved to [dim]{Path.GetDirectoryName(path)}{Path.DirectorySeparatorChar}[/]{fileName}");

        return 0;
    }

    /// <summary>Encodes a block and its witness as a stateless input, recovering missing execution requests.</summary>
    /// <remarks>Recovery mutates the supplied block: it fills transaction senders, marks the header post-merge,
    /// and attaches execution requests, generated access lists and account changes from replay.</remarks>
    internal static async Task<byte[]?> EncodeInput(
        Block block, Witness witness, ISpecProvider specProvider, CancellationToken cancellationToken = default)
    {
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        if (!ProtocolForkExtensions.TryGetByName(spec.Name, out ProtocolFork fork))
        {
            AnsiConsole.MarkupLine($"[red]Unsupported fork {spec.Name}: the stateless input schema requires a Cancun or later block[/]");
            return null;
        }

        await RecoverExecutionRequests(block, witness, specProvider, cancellationToken);

        // Only Amsterdam has a schema of its own; earlier forks share the current-fork schema.
        bool isAmsterdam = fork == ProtocolFork.Amsterdam;
        byte[] encoded = isAmsterdam
            ? EncodeInput(SszExecutionPayloadAmsterdam.From(block), block, witness, specProvider.ChainId)
            : EncodeInput(SszExecutionPayload.From(block), block, witness, specProvider.ChainId);

        byte[] data = new byte[encoded.Length + sizeof(ushort)];
        BinaryPrimitives.WriteUInt16BigEndian(
            data, (isAmsterdam ? ProtocolFork.Amsterdam : ProtocolFork.Current).ToRevision1SchemaId());
        Buffer.BlockCopy(encoded, 0, data, sizeof(ushort), encoded.Length);
        return data;
    }

    private static byte[] EncodeInput<TExecutionPayload>(
        TExecutionPayload payload, Block block, Witness witness, ulong chainId)
        where TExecutionPayload : SszExecutionPayload, ISszCodec<TExecutionPayload>, new()
    {
        StatelessInput<TExecutionPayload> input = new()
        {
            NewPayloadRequest = NewPayloadRequest<TExecutionPayload>.From(block, payload),
            Witness = ExecutionWitness.From(witness),
            ChainId = chainId,
            PublicKeys = RecoverPublicKeys(block.Transactions, chainId)
        };

        return StatelessInput<TExecutionPayload>.Encode(input);
    }

    private static async Task RecoverExecutionRequests(Block block, Witness witness, ISpecProvider specProvider, CancellationToken cancellationToken)
    {
        // EIP-7685 request bodies are absent from block RLP; only their hash survives debug_getRawBlock.
        if (block.ExecutionRequests is not null || block.Header.RequestsHash is null ||
            block.Header.RequestsHash == ExecutionRequestExtensions.EmptyRequestsHash)
            return;

        cancellationToken.ThrowIfCancellationRequested();
        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        if (!spec.RequestsEnabled)
            throw new InvalidDataException($"Cannot recover execution requests for block {block.Number}: {spec.Name} does not enable execution requests. Check the configured fork schedule.");

        using ArrayPoolList<BlockHeader> headers = witness.DecodeHeaders();
        if (headers.Count == 0 || headers[^1].Hash != block.ParentHash)
            throw new InvalidDataException("Witness is missing the block's parent header.");

        if (spec.IsEip4844Enabled && !KzgPolynomialCommitments.IsInitialized)
            await KzgPolynomialCommitments.InitializeAsync().WaitAsync(cancellationToken);

        EthereumEcdsa ecdsa = new(specProvider.ChainId);
        foreach (Transaction tx in block.Transactions)
            tx.SenderAddress ??= ecdsa.RecoverAddress(tx);

        // Requests are post-merge, but the RLP header does not carry this execution flag.
        block.Header.IsPostMerge = true;
        StatelessBlockProcessingEnv env = new(witness, specProvider, Always.Valid, NullLogManager.Instance)
        {
            ExecutionRequestsProcessorFactory = ExecutionRequestsProcessorFactory.Instance
        };
        using IDisposable scope = env.WorldState.BeginScope(headers[^1]);
        // Normal processed-block validation checks the recovered requests hash against the original header.
        env.BlockProcessor.ProcessOne(block, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, spec, cancellationToken);
    }

    private static async Task<(Block?, Witness?, ulong? chainId)> FetchData(string blockParam, Uri host, CancellationToken cancellationToken)
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
                cancellationToken.ThrowIfCancellationRequested();

                string? rlpHex = await client.Post<string>("debug_getRawBlock", EnsureIsHexIfNumber(blockParam));

                if (string.IsNullOrEmpty(rlpHex))
                {
                    AnsiConsole.MarkupLine($"[red]Block not found[/]");
                    return;
                }

                cancellationToken.ThrowIfCancellationRequested();

                byte[] rlp = Convert.FromHexString(rlpHex![2..]);

                IRlpDecoder<Block> blockDecoder = Rlp.GetDecoderOrThrow<Block>();
                RlpReader blockContext = new(rlp);
                Block? decodedBlock = blockDecoder.Decode(ref blockContext, RlpBehaviors.None);
                blockContext.Check(rlp.Length);

                if (decodedBlock is null)
                {
                    AnsiConsole.MarkupLine("[red]Block decoded as null[/]");
                    return;
                }

                block = decodedBlock;

                string blockNumber = EnsureBlockParamIsNumber(blockParam, decodedBlock);

                AnsiConsole.MarkupLine($"[green]✓[/] Fetched block {blockNumber}: {rlp.Length:N0} bytes");

                ctx.Status = $"[orange1]Fetching witness for block {blockNumber}[/]";

                cancellationToken.ThrowIfCancellationRequested();

                witness = await client.Post<Witness>("debug_executionWitness", $"0x{decodedBlock.Number:x}");

                if (witness is null)
                {
                    AnsiConsole.MarkupLine($"[red]Witness not found[/]");
                    return;
                }

                AnsiConsole.MarkupLine(
                    $"[green]✓[/] Fetched witness for block {blockNumber}: {GetWitnessSize(witness):N0} bytes");

                ctx.Status = $"[orange1]Fetching chainId id[/]";

                cancellationToken.ThrowIfCancellationRequested();

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

    private static SszPublicKey[] RecoverPublicKeys(ReadOnlySpan<Transaction> transactions, ulong chainId)
    {
        EthereumEcdsa ecdsa = new(chainId);
        SszPublicKey[] publicKeys = new SszPublicKey[transactions.Length];

        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction tx = transactions[i];
            PublicKey publicKey = ecdsa.RecoverPublicKey(tx)
                ?? throw new InvalidOperationException($"Failed to recover public key for transaction {tx.Hash}");

            publicKeys[i] = SszPublicKey.FromSpan(publicKey.PrefixedBytes);
        }

        return publicKeys;
    }
}
