// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.ComponentModel;
using System.Reflection;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Mcp.Plugin.Tools;

namespace Nethermind.Mcp.Plugin;

/// <summary>MCP prompts: ready-made investigation recipes telling the agent which tools to call, in which order, and how to answer.</summary>
/// <remarks>
/// Arguments are validated with the tool parsers and only their canonical form is embedded, so a prompt argument cannot smuggle
/// free text into the instructions. Invalid arguments fail the <c>prompts/get</c> request with an invalid-params error.
/// </remarks>
[McpServerPromptType]
public sealed class McpPrompts(McpChainProfile profile)
{
    /// <summary>Creates the MCP prompt descriptors for every method annotated with <see cref="McpServerPromptAttribute"/>.</summary>
    internal IReadOnlyList<McpServerPrompt> CreateServerPrompts()
    {
        List<McpServerPrompt> prompts = [];
        foreach (MethodInfo method in typeof(McpPrompts).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            if (method.GetCustomAttribute<McpServerPromptAttribute>() is not null)
                prompts.Add(McpServerPrompt.Create(method, this));
        }

        return prompts;
    }

    /// <summary>Recipe to explain a transaction end to end.</summary>
    [McpServerPrompt(Name = "investigate_transaction", Title = "Investigate a transaction")]
    [Description("Explain what a transaction did: transfers, token movements, contract calls, fees and outcome.")]
    public PromptMessage InvestigateTransaction([Description("The transaction hash (0x + 64 hex digits).")] string hash)
    {
        string tx = ParseHash(hash, nameof(hash));
        return User($"""
            Investigate transaction {tx} on {Network}.

            1. Call `explain_transaction` with hash {tx}. It returns the status, sender, recipient, value, fee and decoded events (token transfers, approvals, swaps).
            2. If the transaction called contracts, call `trace_transaction` with hash {tx} to see internal calls and value transfers between contracts.
            3. For any unfamiliar address involved, call `lookup_address` to say whether it is a contract, a token or an ENS name.
            4. If a tool returns `not_found`, say the transaction is unknown to this node (wrong network or not yet mined). If it returns `unavailable`, the node has pruned that history: say so, and suggest an archive node.

            {AnswerStyle}
            Answer with: a one-sentence summary; then who sent what to whom (native {Symbol} and tokens, with formatted amounts and symbols); the contracts called and why; the fee paid in {Symbol}; and the outcome (success or the revert reason).
            """);
    }

    /// <summary>Recipe to diagnose a failed transaction.</summary>
    [McpServerPrompt(Name = "why_did_my_transaction_fail", Title = "Why did my transaction fail?")]
    [Description("Find why a transaction failed (revert reason, out of gas, failing inner call) and suggest a fix.")]
    public PromptMessage WhyDidMyTransactionFail([Description("The transaction hash (0x + 64 hex digits).")] string hash)
    {
        string tx = ParseHash(hash, nameof(hash));
        return User($"""
            Find out why transaction {tx} on {Network} failed.

            1. Call `explain_transaction` with hash {tx}. Check the status; if it succeeded, tell the user it did not fail and stop.
            2. Read the decoded revert reason (`Error(string)`, `Panic(uint256)` or a custom error). Compare gas used with the gas limit: equal means out of gas.
            3. Call `trace_transaction` with hash {tx} to find the innermost call that reverted first and which contract it was.
            4. If a fix is plausible (more gas, a missing approval, a wrong amount), call `simulate_transaction` with the corrected parameters at `latest` to check it would now succeed. Say clearly that a simulation is not a guarantee.
            5. On `unavailable`, the node pruned that history: say so and suggest an archive node.

            {AnswerStyle}
            Answer with: the failure in one sentence, the evidence (revert reason, failing call), what the user can do, and the fee that was still paid in {Symbol}.
            """);
    }

    /// <summary>Recipe to profile an address.</summary>
    [McpServerPrompt(Name = "summarize_address", Title = "Summarize an address")]
    [Description("Describe an address: account type, native balance, well-known token holdings, contract details and ENS name.")]
    public PromptMessage SummarizeAddress([Description("The address (0x + 40 hex digits).")] string address)
    {
        string account = ParseAddress(address, nameof(address));
        return User($"""
            Summarize address {account} on {Network}.

            1. Call `lookup_address` with {account}: account type (externally owned or contract), ENS name, and token metadata if it is a token.
            2. Call `get_balance` with {account} at `latest` for the native {Symbol} balance.
            3. Call `token_balances` with owner {account} for its holdings of well-known tokens.
            4. If it is a contract and the user wants more, call `get_code` (size only; do not dump bytecode) and `get_logs` over a small recent block range to show recent activity.

            {AnswerStyle}
            Answer with: what the address is, its {Symbol} balance, notable token balances (formatted, with symbols), and anything noteworthy. Never present unverified token names as genuine.
            """);
    }

    /// <summary>Recipe to check node health.</summary>
    [McpServerPrompt(Name = "check_node_health", Title = "Check node health")]
    [Description("Check whether the node is synced, following the chain head, and which history it can serve.")]
    public PromptMessage CheckNodeHealth() => User($"""
        Check the health of this Nethermind node on {Network}.

        1. Call `node_status`: sync state, head block and its age, peers, and the oldest block with state, bodies and receipts.
        2. Call `chain_info` and confirm the chain id is {profile.ChainId}.
        3. Call `block_summary` for `latest` and compare its timestamp with the current time: more than a minute behind means the node is lagging.

        {AnswerStyle}
        Answer with: healthy or not, in one line; then sync state, head age, peer count, and the history range available (for example "state for the last 128 blocks only: a pruned node"). Suggest concrete next steps for any problem, but remember that this server cannot change the node.
        """);

    /// <summary>Recipe for gas price advice.</summary>
    [McpServerPrompt(Name = "gas_advice", Title = "Gas price advice")]
    [Description("Recommend max fee and priority fee for a transaction now, with the expected cost.")]
    public PromptMessage GasAdvice() => User($"""
        Give gas price advice for sending a transaction on {Network} now.

        1. Call `fee_estimate`: current base fee, the trend over recent blocks, and priority-fee percentiles.
        2. If the user described a specific transaction, call `estimate_gas` for it; otherwise use 21000 gas for a plain {Symbol} transfer.

        {AnswerStyle}
        Answer with: a suggested `maxPriorityFeePerGas` and `maxFeePerGas` in gwei for slow, normal and fast inclusion; the expected cost in {Symbol} for the gas amount (gas × (base fee + priority fee)); and whether fees are rising or falling. Keep it short.
        """);

    /// <summary>Recipe to report token holdings.</summary>
    [McpServerPrompt(Name = "token_report", Title = "Token report")]
    [Description("Report the native and well-known token balances of an owner address.")]
    public PromptMessage TokenReport([Description("The owner address (0x + 40 hex digits).")] string owner)
    {
        string account = ParseAddress(owner, nameof(owner));
        return User($"""
            Report the token holdings of {account} on {Network}.

            1. Call `get_balance` with {account} for the native {Symbol} balance.
            2. Call `token_balances` with owner {account}. It checks the well-known tokens of this chain (see the `nethermind://contracts` resource).
            3. For any other token the user asks about, call `token_info` for its name, symbol and decimals, then `call_function` with `balanceOf(address)`.

            {AnswerStyle}
            Answer with a table: token, symbol, balance (formatted with the token's decimals), and contract address. List the native {Symbol} first and omit zero balances unless asked.
            """);
    }

    private string Network => $"{profile.NetworkName} (chain id {profile.ChainId}{(profile.IsTestnet ? ", testnet" : "")})";

    private string Symbol => profile.NativeCurrencySymbol;

    private string AnswerStyle => profile.IsGnosisFamily
        ? "Present amounts in human units with symbols: the native currency here is xDAI (not ETH), 18 decimals; GNO is the staking token, an ERC-20. Show gas prices in gwei. Name the network in the answer."
        : $"Present amounts in human units with symbols: the native currency is {Symbol}, 18 decimals. Show gas prices in gwei. Name the network in the answer{(profile.IsTestnet ? " and that testnet coins have no value" : "")}.";

    private static PromptMessage User(string text) => new() { Role = Role.User, Content = new TextContentBlock { Text = text } };

    private static string ParseHash(string? value, string parameter) =>
        McpToolInput.TryParseHash(value, parameter, out Hash256? parsed, out string? error)
            ? parsed.ToString()
            : throw new McpProtocolException(error, McpErrorCode.InvalidParams);

    private static string ParseAddress(string? value, string parameter) =>
        McpToolInput.TryParseAddress(value, parameter, out Address? parsed, out string? error)
            ? McpEthHelpers.Checksum(parsed)
            : throw new McpProtocolException(error, McpErrorCode.InvalidParams);
}
