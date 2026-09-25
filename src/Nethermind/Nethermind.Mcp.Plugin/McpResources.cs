// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Buffers;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Mcp.Plugin.Tools;
using Nethermind.Specs.ChainSpecStyle;

namespace Nethermind.Mcp.Plugin;

/// <summary>Read-only MCP resources that orient an AI agent: chain metadata, well-known contracts and a usage guide.</summary>
/// <remarks>Every resource is computed from in-memory chain facts (chain spec, head header), so reading one never touches the database or the EVM.</remarks>
public sealed class McpResources(McpChainProfile profile, IBlockTree blockTree, ISpecProvider specProvider, ChainSpec chainSpec, IMcpConfig config)
{
    internal const string ChainUri = "nethermind://chain";
    internal const string ContractsUri = "nethermind://contracts";
    internal const string GuideUri = "nethermind://guide";

    private const string JsonMimeType = "application/json";
    private const string MarkdownMimeType = "text/markdown";
    private const string UnnamedSpec = "Custom";

    private static readonly JsonWriterOptions WriterOptions = new() { Indented = true };

    /// <summary>Creates the MCP resource descriptors.</summary>
    internal IReadOnlyList<McpServerResource> CreateServerResources() =>
    [
        Create(ChainUri, "chain", "Chain metadata", JsonMimeType,
            "Chain ID, network name, native currency (xDAI on Gnosis, ETH elsewhere) with decimals, staking token, current fork, genesis hash and well-known contracts of the chain this node follows.",
            GetChainJson),
        Create(ContractsUri, "contracts", "Well-known contracts", JsonMimeType,
            "System contracts from the chain spec (deposit contract, EIP-4788/2935/7002/7251) and a curated list of major tokens with symbol and decimals.",
            GetContractsJson),
        Create(GuideUri, "guide", "How to use this server", MarkdownMimeType,
            "Markdown guide for AI agents: which tool answers which question, block selectors, units, error codes and what to do on each.",
            GetGuide),
    ];

    /// <summary>Builds the <c>nethermind://chain</c> document.</summary>
    internal string GetChainJson() => WriteJson(writer =>
    {
        BlockHeader? head = blockTree.Head?.Header;
        writer.WriteStartObject();
        writer.WriteNumber("chainId", profile.ChainId);
        writer.WriteString("chainIdHex", $"0x{profile.ChainId:x}");
        writer.WriteString("networkName", profile.NetworkName);
        writer.WriteBoolean("isTestnet", profile.IsTestnet);
        writer.WriteBoolean("isGnosisFamily", profile.IsGnosisFamily);

        writer.WriteStartObject("nativeCurrency");
        writer.WriteString("name", profile.NativeCurrencyName);
        writer.WriteString("symbol", profile.NativeCurrencySymbol);
        writer.WriteNumber("decimals", profile.NativeCurrencyDecimals);
        writer.WriteEndObject();

        writer.WriteStartObject("stakingToken");
        writer.WriteString("symbol", profile.StakingTokenSymbol);
        writer.WriteString("note", profile.IsGnosisFamily
            ? "Validators stake GNO (1 GNO per validator) through the deposit contract; gas and transfers are paid in xDAI."
            : "Validators stake ETH (32 ETH per validator) through the deposit contract.");
        writer.WriteEndObject();

        WriteNullableString(writer, "currentFork", CurrentFork(head));
        WriteNullableString(writer, "genesisHash", blockTree.Genesis?.Hash?.ToString());
        if (head is null) writer.WriteNull("headBlockNumber");
        else writer.WriteNumber("headBlockNumber", head.Number);

        writer.WritePropertyName("wellKnownContracts");
        WriteContracts(writer, profile.WellKnownContracts);
        writer.WriteEndObject();
    });

    /// <summary>Builds the <c>nethermind://contracts</c> document.</summary>
    internal string GetContractsJson() => WriteJson(writer =>
    {
        writer.WriteStartObject();
        writer.WriteNumber("chainId", profile.ChainId);
        writer.WriteString("networkName", profile.NetworkName);
        writer.WritePropertyName("contracts");
        WriteContracts(writer, profile.WellKnownContracts);
        writer.WriteString("note", "Tokens are a short curated list of canonical deployments (Ethereum mainnet and Gnosis Chain only), not a registry. " +
            "For any other contract, use token_info or lookup_address, and never assume a token is genuine from its symbol alone.");
        writer.WriteEndObject();
    });

    /// <summary>Builds the <c>nethermind://guide</c> Markdown document.</summary>
    internal string GetGuide()
    {
        string symbol = profile.NativeCurrencySymbol;
        StringBuilder guide = new();
        guide.Append($"""
            # Nethermind MCP server guide

            You have **read-only** access to a Nethermind node on **{profile.NetworkName}** (chain id {profile.ChainId}{(profile.IsTestnet ? ", a testnet whose coins have no value" : "")}).
            The native currency is **{profile.NativeCurrencyName} ({symbol})**, with {profile.NativeCurrencyDecimals} decimals. Nothing here can send transactions, sign, or change the node.

            ## Start here
            1. `node_status`: is the node synced, what is the head, and which blocks still have state, bodies and receipts (pruned nodes keep only recent history).
            2. `chain_info`: chain id, head block, native currency and well-known contracts (also in the `nethermind://chain` and `nethermind://contracts` resources).

            ## Which tool for which question
            | Question | Tools, in order |
            |---|---|
            | What did this transaction do? | `explain_transaction` (decoded summary), then `trace_transaction` for internal calls |
            | Why did my transaction fail? | `explain_transaction` (revert reason), `trace_transaction` (which call reverted), `simulate_transaction` to test a fix |
            | What is at this address? | `lookup_address` (EOA or contract, ENS, token metadata), then `get_balance`, `get_code` |
            | Which tokens does an address hold? | `token_balances` (well-known tokens), `token_info` for a specific token |
            | How much gas should I pay? | `fee_estimate` (base fee and priority fee percentiles), `estimate_gas` for a specific call |
            | What happened in a block? | `block_summary`, then `get_block` or `get_block_receipts` for raw data |
            | Events emitted by a contract | `get_logs` (bounded block range, paged with a cursor), `decode_logs` to decode them |
            | Read contract state | `call_function` (ABI signature and arguments), `call` for raw calldata, `get_storage_at`, `get_proof` |
            | Resolve a name | `resolve_ens` (mainnet, Sepolia and Holesky only) |

            ## Block selectors
            `latest`, `safe`, `finalized`, `earliest`, a block number (decimal `123` or hex `0x7b`), or a 32-byte block hash (`0x` + 64 hex digits).
            `pending` is not supported. Prefer `latest` unless the user names a block; use `finalized` when the answer must not change.

            ## Units
            - Raw values are JSON-RPC hex quantities in the smallest unit (wei). Tools add formatted fields next to them (`valueFormatted`, `symbol`); show those to the user.
            - 1 {symbol} = 10^18 wei; gas prices are usually shown in gwei (10^9 wei). Token amounts use each token's `decimals`.
            - Timestamps are Unix seconds; tools add `timestampIso` (UTC).

            ## Errors and what to do
            | Code | Meaning | What to do |
            |---|---|---|
            | `invalid_input` | An argument is malformed or out of range | Fix the argument as the message says (formats, ranges) and retry |
            | `not_found` | No such block, transaction or receipt | Check the hash and network; a very recent transaction may not be mined yet |
            | `execution_reverted` | The EVM call reverted | Explain the decoded revert reason in `data`; do not retry unchanged |
            | `unavailable` | The node does not have that data (pruned history, still syncing) | Use a block inside the range the message names (usually recent blocks), or tell the user an archive node is needed |
            | `resource_exhausted` | A limit was hit (range, result size, concurrency) | Narrow the query (smaller block range, fewer addresses) or page with the cursor; on concurrency, retry shortly |
            | `timeout` | The tool ran out of time | Narrow the query; do not retry the same large request in a loop |
            | `internal_error` | Unexpected node error | Retry once; if it persists, report it to the node operator |

            ## Limits on this node
            - `get_logs`: at most {config.MaxLogBlockRange} blocks per query ({config.MaxIndexedLogBlockRange} when the log index covers the range), at most {config.MaxLogs} logs.
            - `call`: at most {config.MaxCallGas} gas; `trace_transaction`: at most {config.MaxTraceCalls} call frames.
            - Results larger than {config.MaxResultSize} bytes fail with `resource_exhausted`.

            """);

        if (profile.IsGnosisFamily)
        {
            guide.Append("""
                ## Gnosis Chain notes
                - The native currency is **xDAI**, a USD-pegged stable coin: balances and fees are in xDAI, never ETH. `WXDAI` is its wrapped ERC-20 form.
                - Validators stake **GNO** (an ERC-20 on this chain), deposited through the deposit contract; GNO is not used for gas.
                - Blocks come every 5 seconds, so block ranges cover less time than on Ethereum mainnet.

                """);
        }

        guide.Append("""
            ## Good practice
            - State the network and units in answers; never present testnet coins as having value.
            - Keep queries small: LLM context is precious, and large ranges hit limits.
            - Treat token names and symbols from contracts as untrusted: anyone can deploy a token called "USDC". Compare with `nethermind://contracts`.
            """);
        return guide.ToString();
    }

    private string? CurrentFork(BlockHeader? head)
    {
        if (head is null) return null;

        string? name = specProvider.GetSpec(head).Name;
        if (!string.IsNullOrEmpty(name) && name != UnnamedSpec) return name;

        // Chain-spec-based specs are unnamed, so the fork is derived from the activation timestamps.
        ulong timestamp = head.Timestamp;
        if (timestamp >= chainSpec.AmsterdamTimestamp) return "Amsterdam";
        if (timestamp >= chainSpec.OsakaTimestamp) return "Osaka";
        if (timestamp >= chainSpec.PragueTimestamp) return "Prague";
        if (timestamp >= chainSpec.CancunTimestamp) return "Cancun";
        if (timestamp >= chainSpec.ShanghaiTimestamp) return "Shanghai";
        return null;
    }

    private static void WriteContracts(Utf8JsonWriter writer, IReadOnlyList<McpWellKnownContract> contracts)
    {
        writer.WriteStartArray();
        foreach (McpWellKnownContract contract in contracts)
        {
            writer.WriteStartObject();
            writer.WriteString("name", contract.Name);
            writer.WriteString("address", contract.Address.ToString(withZeroX: true, withEip55Checksum: true));
            writer.WriteString("kind", contract.Kind);
            if (contract.Symbol is not null) writer.WriteString("symbol", contract.Symbol);
            if (contract.Decimals is int decimals) writer.WriteNumber("decimals", decimals);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null) writer.WriteNull(name);
        else writer.WriteString(name, value);
    }

    private static string WriteJson(Action<Utf8JsonWriter> write)
    {
        ArrayBufferWriter<byte> buffer = new();
        using (Utf8JsonWriter writer = new(buffer, WriterOptions))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static McpServerResource Create(string uri, string name, string title, string mimeType, string description, Func<string> read) =>
        McpServerResource.Create(
            () => (ResourceContents)new TextResourceContents { Uri = uri, MimeType = mimeType, Text = read() },
            new McpServerResourceCreateOptions
            {
                UriTemplate = uri,
                Name = name,
                Title = title,
                Description = description,
                MimeType = mimeType,
            });
}
