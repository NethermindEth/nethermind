// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Blockchain.Tracing;
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
using Nethermind.Merge.Plugin.SszRest;
using Nethermind.Serialization.Json;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using Nethermind.Stateless.Execution.IO;
using Spectre.Console;
using System.Buffers.Binary;
using System.Globalization;

namespace Nethermind.StatelessInputGen;

internal static class InputGenerator
{
    internal static void ConvertToSsz(string filename)
    {
        ReadOnlySpan<byte> data = File.ReadAllBytes(filename);
        ulong dataLen = BinaryPrimitives.ReadUInt64LittleEndian(data);
        data = data.Slice(sizeof(ulong), checked((int)dataLen));

        (Block block, Witness witness, ulong chainId) = InputSerializer.Deserialize(data);

        StatelessInput<SszExecutionPayloadV3> input;

        using (witness)
        {
            RecoverExecutionRequests(block, witness, GetSpecProvider(chainId), chainId);
            NewPayloadRequest<SszExecutionPayloadV3> request = NewPayloadRequest<SszExecutionPayloadV3>.From(block);
            input = new()
            {
                NewPayloadRequest = request,
                Witness = ExecutionWitness.From(witness),
                ChainConfig = new()
                {
                    ChainId = chainId,
                    ActiveFork = ForkConfig.From(block.Header, GetSpecProvider(chainId))
                },
                // The guest sets each tx's SenderAddress from these and skips execution entirely
                // when their count doesn't match the tx count, so they must be recovered here.
                PublicKeys = RecoverPublicKeys(block.Transactions, chainId),
            };
        }

        byte[] encoded = StatelessInput<SszExecutionPayloadV3>.Encode(input);
        byte[] versioned = new byte[encoded.Length + sizeof(ushort)];

        BinaryPrimitives.WriteUInt16BigEndian(versioned, 0);
        Buffer.BlockCopy(encoded, 0, versioned, sizeof(ushort), encoded.Length);

        {
            int rem = versioned.Length % sizeof(ulong);
            int len = sizeof(ulong) + versioned.Length + (rem == 0 ? 0 : (sizeof(ulong) - rem));
            byte[] framedData = new byte[len];

            BinaryPrimitives.WriteUInt64LittleEndian(framedData, (ulong)versioned.Length);
            Buffer.BlockCopy(versioned, 0, framedData, sizeof(ulong), versioned.Length);
            encoded = framedData;
        }

        string dir = Path.GetDirectoryName(filename) ?? string.Empty;
        filename = $"{Path.GetFileNameWithoutExtension(filename)}.ssz";

        File.WriteAllBytes(Path.Join(dir, filename), encoded);
    }

    /// <summary>
    /// Generates the .bin from a StatelessValidationFixture JSON without any RPC fetch.
    /// </summary>
    internal static int GenerateFromFixture(
        string fixturePath, string output, bool forZisk, bool chainConfigEnvelope)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fixturePath);

        (Block block, Witness witness, ulong chainId, byte[]? envelope, System.Text.Json.JsonElement chainConfigJson) =
            FixtureReader.Read(fixturePath, includeChainConfigEnvelope: chainConfigEnvelope);

        AnsiConsole.MarkupLine(
            $"[green]✓[/] Loaded fixture [dim]{Markup.Escape(Path.GetFileName(fixturePath))}[/]: " +
            $"block #{block.Number}, chainId={chainId}, " +
            $"witness state={witness.State.Count} codes={witness.Codes.Count} headers={witness.Headers.Count}");

        block.Header.Hash ??= Keccak.Compute(Rlp.Encode(block.Header).Bytes);

        // The chain_config envelope is no longer carried in the SSZ input. The
        // StatelessInput / ChainConfig layout is a fixed Ethereum SSZ contract
        // and must stay byte-compatible with already-published inputs, so we
        // cannot add a variable-length field to ChainConfig. If a fixture still
        // requests it, warn and fall back to the hardcoded chain map.
        if (envelope is { Length: > 0 })
            AnsiConsole.MarkupLine(
                "[yellow]![/] chain_config envelope present but ignored: the SSZ ChainConfig " +
                "format is fixed; the guest selects the spec from ChainId + ActiveFork.");

        byte[] data;
        ChainConfig configForResult;
        using (witness)
        {
            // Synthetic EEST fixtures carry timestamps near 0, so ForkConfig.From would map them to
            // pre-Frontier and the guest would reject the block. Detect the target fork from
            // chain_config and synthesize a header at its mainnet activation so the right spec is picked.
            BlockHeader headerForFork = MaybeOverrideHeader(block.Header, chainConfigJson, chainId);
            // From(headerForFork) detects the right Fork (+ BlobSchedule), but it also stamps the
            // fork's *mainnet* activation point. The guest's StatelessSpecProvider applies the active
            // fork only to blocks at/after that activation, so a synthetic block at timestamp ~0 would
            // fall back to a pre-fork spec. Re-stamp the activation to the block's own point so the
            // detected fork actually applies to it.
            ForkConfig activeFork = ForkConfig.From(headerForFork, GetSpecProvider(chainId));
            activeFork.Activation = SszForkActivation.From(new Nethermind.Core.Specs.ForkActivation(block.Header.Number, block.Header.Timestamp));
            ChainConfig chainConfig = new()
            {
                ChainId = chainId,
                ActiveFork = activeFork
            };
            configForResult = chainConfig;

            RecoverExecutionRequests(block, witness, GetSpecProvider(chainId), chainId);

            StatelessInput<SszExecutionPayloadV3> input = new()
            {
                NewPayloadRequest = NewPayloadRequest<SszExecutionPayloadV3>.From(block),
                Witness = ExecutionWitness.From(witness),
                ChainConfig = chainConfig,
                // The guest sets each tx's SenderAddress from these and skips execution entirely
                // when their count doesn't match the tx count, so they must be recovered here.
                PublicKeys = RecoverPublicKeys(block.Transactions, chainId),
            };

            byte[] encoded = StatelessInput<SszExecutionPayloadV3>.Encode(input);
            data = new byte[encoded.Length + sizeof(ushort)];

            BinaryPrimitives.WriteUInt16BigEndian(data, 0);
            Buffer.BlockCopy(encoded, 0, data, sizeof(ushort), encoded.Length);
        }

        if (forZisk)
            data = ApplyZiskFrame(data);

        Directory.CreateDirectory(output);
        string fileName = $"{block.Number}.bin";
        string path = Path.Join(output, fileName);
        File.WriteAllBytes(path, data);

        // Emit a sibling `<num>.hash` containing the SSZ-encoded
        // StatelessValidationResult that the guest is expected to write to
        // stdout on successful validation. The host-side benchmark runner
        // compares the guest's public values byte-for-byte against this.
        NewPayloadRequest<SszExecutionPayloadV3>.Merkleize(
            NewPayloadRequest<SszExecutionPayloadV3>.From(block),
            out Nethermind.Int256.UInt256 newPayloadRoot);
        StatelessValidationResult expected = new()
        {
            NewPayloadRequestRoot = new Hash256(newPayloadRoot.ToLittleEndian()),
            IsSuccess = true,
            ChainConfig = configForResult
        };
        byte[] expectedBytes = StatelessValidationResult.Encode(expected);
        string hashPath = Path.Join(output, $"{block.Number}.hash");
        File.WriteAllBytes(hashPath, expectedBytes);

        AnsiConsole.MarkupLine($"[green]✓[/] Saved to [dim]{Path.GetDirectoryName(path)}{Path.DirectorySeparatorChar}[/]{fileName} (expected {expectedBytes.Length} bytes)");
        return 0;
    }

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

            RecoverExecutionRequests(block, witness, GetSpecProvider(chainId.Value), chainId.Value);

            StatelessInput<SszExecutionPayloadV3> input = new()
            {
                NewPayloadRequest = NewPayloadRequest<SszExecutionPayloadV3>.From(block),
                Witness = ExecutionWitness.From(witness),
                ChainConfig = new()
                {
                    ChainId = chainId.Value,
                    ActiveFork = ForkConfig.From(block.Header, GetSpecProvider(chainId.Value))
                },
                // The guest sets each tx's SenderAddress from these and skips execution entirely
                // when their count doesn't match the tx count, so they must be recovered here.
                PublicKeys = RecoverPublicKeys(block.Transactions, chainId.Value),
            };

            byte[] encoded = StatelessInput<SszExecutionPayloadV3>.Encode(input);
            data = new byte[encoded.Length + sizeof(ushort)];

            BinaryPrimitives.WriteUInt16BigEndian(data, 0);

            Buffer.BlockCopy(encoded, 0, data, sizeof(ushort), encoded.Length);
        }

        if (forZisk)
            data = ApplyZiskFrame(data);

        Directory.CreateDirectory(output);

        string fileName = $"{EnsureBlockParamIsNumber(blockParam, block)}.ssz";
        string path = Path.Join(output, fileName);

        File.WriteAllBytes(path, data);

        AnsiConsole.MarkupLine($"[green]✓[/] Saved to [dim]{Path.GetDirectoryName(path)}{Path.DirectorySeparatorChar}[/]{fileName}");

        return 0;
    }

    /// <summary>
    /// Wraps <paramref name="data"/> in the Zisk input frame: an 8-byte little-endian
    /// length prefix followed by the payload, padded with zero bytes so the total
    /// length is a multiple of <c>sizeof(ulong)</c>.
    /// </summary>
    private static byte[] ApplyZiskFrame(byte[] data)
    {
        int rem = data.Length % sizeof(ulong);
        int len = sizeof(ulong) + data.Length + (rem == 0 ? 0 : sizeof(ulong) - rem);
        byte[] framed = new byte[len];
        BinaryPrimitives.WriteUInt64LittleEndian(framed, (ulong)data.Length);
        Buffer.BlockCopy(data, 0, framed, sizeof(ulong), data.Length);
        return framed;
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
        _ => $"Not supported ({chainId})"
    };

    internal static ISpecProvider GetSpecProvider(ulong chainId) => chainId switch
    {
        BlockchainIds.Hoodi => HoodiSpecProvider.Instance,
        BlockchainIds.Mainnet => MainnetSpecProvider.Instance,
        BlockchainIds.Sepolia => SepoliaSpecProvider.Instance,
        _ => throw new ArgumentException($"Unsupported chainId id: {chainId}", nameof(chainId))
    };

    private static BlockHeader MaybeOverrideHeader(BlockHeader original, System.Text.Json.JsonElement chainConfig, ulong chainId)
    {
        // Only mainnet ChainId 1 gets the override (Hoodi/Sepolia not handled).
        if (chainId != 1UL) return original;
        // Order matters: latest entry wins.
        (string Field, Nethermind.Core.Specs.ForkActivation Activation)[] order =
        {
            ("shanghai_time", Nethermind.Specs.MainnetSpecProvider.ShanghaiActivation),
            ("cancun_time",   Nethermind.Specs.MainnetSpecProvider.CancunActivation),
            ("prague_time",   Nethermind.Specs.MainnetSpecProvider.PragueActivation),
            ("osaka_time",    Nethermind.Specs.MainnetSpecProvider.OsakaActivation),
        };
        Nethermind.Core.Specs.ForkActivation? latest = null;
        foreach ((string field, Nethermind.Core.Specs.ForkActivation activation) in order)
        {
            if (!chainConfig.TryGetProperty(field, out System.Text.Json.JsonElement el)) continue;
            if (el.ValueKind == System.Text.Json.JsonValueKind.Null) continue;
            ulong t = el.GetUInt64();
            if (t <= original.Timestamp) latest = activation;
        }
        if (latest is null) return original;
        BlockHeader synthetic = new(
            original.ParentHash ?? Keccak.Zero, original.UnclesHash ?? Keccak.OfAnEmptySequenceRlp,
            original.Beneficiary ?? Address.Zero, original.Difficulty, latest.Value.BlockNumber, original.GasLimit, latest.Value.Timestamp!.Value, original.ExtraData ?? Array.Empty<byte>())
        { Hash = original.Hash };
        return synthetic;
    }

    /// <summary>
    /// Recovers a block's EIP-7685 execution requests by re-executing it against its witness and
    /// stores them on <paramref name="block"/> so the SSZ payload carries the real requests.
    /// </summary>
    /// <remarks>
    /// Execution requests are part of neither the block RLP (<c>debug_getRawBlock</c>) nor the
    /// fixture body — the header only carries <c>RequestsHash</c>. A block decoded from those
    /// sources therefore has <c>ExecutionRequests == null</c>, and <see cref="NewPayloadRequest{T}.From"/>
    /// would encode an empty requests list, producing an empty <c>RequestsHash</c> on the guest side.
    /// For any block that actually contains requests (e.g. EIP-7002 withdrawal / EIP-7251
    /// consolidation requests, which are drained from system contracts and never appear in logs)
    /// that mismatches the real header hash and the guest rejects the block with InvalidHeaderHash.
    /// Re-executing the block over the witness reproduces the requests exactly, as the guest does.
    /// </remarks>
    private static void RecoverExecutionRequests(Block block, Witness witness, ISpecProvider specProvider, ulong chainId)
    {
        // Pre-Prague blocks have no requests field; blocks with the empty-requests hash carry no
        // requests, and From() reproduces that empty hash without re-execution. Only blocks that
        // actually contain requests need the (expensive) re-execution to recover them.
        if (block.Header.RequestsHash is null || block.Header.RequestsHash == ExecutionRequestExtensions.EmptyRequestsHash)
            return;

        IReleaseSpec spec = specProvider.GetSpec(block.Header);
        if (spec.IsEip4844Enabled && !KzgPolynomialCommitments.IsInitialized)
            KzgPolynomialCommitments.InitializeAsync().GetAwaiter().GetResult();

        // The block processor needs sender addresses; recover any that are not already set.
        EthereumEcdsa ecdsa = new(chainId);
        foreach (Transaction tx in block.Transactions)
            tx.SenderAddress ??= ecdsa.RecoverAddress(tx);

        using ArrayPoolList<BlockHeader> headers = witness.DecodeHeaders();
        BlockHeader parentHeader = headers[^1];

        StatelessBlockProcessingEnv env = new(witness, specProvider, Always.Valid, NullLogManager.Instance);
        using IDisposable scope = env.WorldState.BeginScope(parentHeader);
        (Block processed, _) = env.BlockProcessor.ProcessOne(
            block, ProcessingOptions.ReadOnlyChain, NullBlockTracer.Instance, spec);

        block.ExecutionRequests = processed.ExecutionRequests;
    }

    private static SszPublicKeys[] RecoverPublicKeys(ReadOnlySpan<Transaction> transactions, ulong chainId)
    {
        EthereumEcdsa ecdsa = new(chainId);
        SszPublicKeys[] publicKeys = new SszPublicKeys[transactions.Length];
        for (int i = 0; i < transactions.Length; i++)
        {
            Transaction tx = transactions[i];
            PublicKey publicKey = ecdsa.RecoverPublicKey(tx)
                ?? throw new InvalidOperationException($"Failed to recover public key for transaction {tx.Hash}");
            publicKeys[i] = new() { Bytes = publicKey.PrefixedBytes };
        }
        return publicKeys;
    }
}
