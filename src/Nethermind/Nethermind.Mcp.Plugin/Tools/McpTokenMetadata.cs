// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Globalization;
using System.Numerics;
using System.Text;
using Nethermind.Blockchain.Find;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.JsonRpc.Modules.Eth;
using Nethermind.Logging;
using Nethermind.Mcp.Plugin.Tools.Abi;

namespace Nethermind.Mcp.Plugin.Tools;

/// <summary>Token metadata read from the contract; any field may be missing for non-standard tokens.</summary>
public sealed record McpTokenInfo(Address Address, string? Name, string? Symbol, byte? Decimals);

/// <summary>Reads and caches ERC-20/721 metadata (<c>name</c>, <c>symbol</c>, <c>decimals</c>) via <c>eth_call</c>.</summary>
/// <remarks>
/// <para>Callers pass an eth module they already rented. Each metadata call is bounded by <see cref="MetadataCallGas"/>.
/// Both ABI <c>string</c> and legacy <c>bytes32</c> results (such as MKR's) are accepted; texts are stripped of control
/// characters and capped at <see cref="MaxTextLength"/> characters, since they are untrusted contract output shown to an LLM.</para>
/// <para>Results read at the head are cached per chain and address in a bounded LRU of <see cref="CacheCapacity"/> entries
/// for at most <see cref="CacheTtl"/> from the start of the read, so a proxy upgrade or reorg that changes a token's symbol
/// or decimals is picked up again. A read is cached only if the head did not move while it ran and no read that started
/// after it has already been cached, so a slow read of the old state never replaces a newer entry; reads at older blocks bypass the cache, since a token may not exist yet or may have been upgraded since. Results
/// affected by a transient failure (state not available, node busy, unexpected error) are returned but not cached;
/// reverts and malformed return data are permanent and cached as missing fields.</para>
/// </remarks>
/// <param name="logManager">Logs unexpected failures at debug level.</param>
/// <param name="blockFinder">Recognizes head blocks passed by hash or number; without it only <c>latest</c> reads are cached.</param>
/// <param name="timeProvider">Expires cached entries; defaults to the system clock.</param>
public sealed class McpTokenMetadata(ILogManager logManager, IBlockFinder? blockFinder = null, TimeProvider? timeProvider = null)
{
    /// <summary>The maximum number of cached tokens.</summary>
    public const int CacheCapacity = 4096;

    /// <summary>How long a cached token's metadata is served before it is read again.</summary>
    public static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(10);

    /// <summary>The gas limit of each metadata <c>eth_call</c>.</summary>
    public const ulong MetadataCallGas = 150_000;

    /// <summary>The maximum length of a returned name or symbol.</summary>
    public const int MaxTextLength = 64;

    private static readonly byte[] NameSelector = [0x06, 0xfd, 0xde, 0x03];
    private static readonly byte[] SymbolSelector = [0x95, 0xd8, 0x9b, 0x41];
    private static readonly byte[] DecimalsSelector = [0x31, 0x3c, 0xe5, 0x67];
    private static readonly McpAbiParam[] StringOutput = [new(string.Empty, McpAbiType.String)];

    private readonly Lock _lock = new();
    private readonly Dictionary<(ulong ChainId, AddressAsKey Address), LinkedListNode<CacheEntry>> _cache = [];
    private readonly LinkedList<CacheEntry> _order = new();
    private readonly ILogger _logger = logManager.GetClassLogger<McpTokenMetadata>();
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private long _readSequence;

    /// <summary>Creates the metadata reader without logging.</summary>
    public McpTokenMetadata() : this(LimboLogs.Instance, null)
    {
    }

    /// <summary>Gets the number of cached tokens.</summary>
    public int CachedCount
    {
        get
        {
            lock (_lock) return _cache.Count;
        }
    }

    /// <summary>Returns the token metadata at <paramref name="block"/>, or <see langword="null"/> if <paramref name="token"/> has no code.</summary>
    /// <remarks>Never throws for contract misbehaviour; returns <see langword="null"/> also when the code cannot be read (for example state not available).</remarks>
    public McpTokenInfo? Get(IEthRpcModule eth, Address token, BlockParameter block) =>
        TryGet(eth, token, block, null, out McpTokenInfo? info) ? info : null;

    /// <summary>Reads the token metadata at <paramref name="block"/> like <see cref="Get"/>, checking <paramref name="stop"/> before every RPC.</summary>
    /// <returns><see langword="false"/> if <paramref name="stop"/> interrupted the read; nothing is cached then.</returns>
    /// <param name="eth">The rented eth module.</param>
    /// <param name="token">The token contract.</param>
    /// <param name="block">The block to read at.</param>
    /// <param name="stop">Returns <see langword="true"/> once no further RPC may start; cached metadata is still returned.</param>
    /// <param name="info">The metadata, or <see langword="null"/> if the token has no code or it cannot be read.</param>
    internal bool TryGet(IEthRpcModule eth, Address token, BlockParameter block, Func<bool>? stop, out McpTokenInfo? info)
    {
        info = null;
        ResultWrapper<ulong> chainIdResult = eth.eth_chainId();
        ulong chainId = chainIdResult.Result.ResultType == ResultType.Success ? chainIdResult.Data : 0;
        (ulong, AddressAsKey) key = (chainId, token);
        bool cacheable = IsHead(block);
        // Taken before any state is read: a read that starts later sees a head at least as new.
        long sequence = Interlocked.Increment(ref _readSequence);
        DateTimeOffset started = _time.GetUtcNow();
        Hash256? head = blockFinder?.Head?.Hash;
        lock (_lock)
        {
            if (cacheable && _cache.TryGetValue(key, out LinkedListNode<CacheEntry>? node))
            {
                _order.Remove(node);
                if (node.Value.Expires > started)
                {
                    _order.AddFirst(node);
                    info = node.Value.Info;
                    return true;
                }

                _cache.Remove(key);
            }
        }

        try
        {
            if (stop?.Invoke() == true) return false;
            using ResultWrapper<byte[]> code = eth.eth_getCode(token, block);
            if (code.Result.ResultType != ResultType.Success || code.Data is not { Length: > 0 })
            {
                return true;
            }

            bool transient = false;
            if (stop?.Invoke() == true) return false;
            string? name = ReadText(eth, token, block, NameSelector, ref transient);
            if (stop?.Invoke() == true) return false;
            string? symbol = ReadText(eth, token, block, SymbolSelector, ref transient);
            if (stop?.Invoke() == true) return false;
            byte? decimals = ReadDecimals(eth, token, block, ref transient);
            info = new McpTokenInfo(token, name, symbol, decimals);
            // A moved head means the calls may have read different blocks, and the entry would describe neither.
            if (cacheable && !transient && blockFinder?.Head?.Hash == head)
            {
                Add(key, info, sequence, started + CacheTtl);
            }

            return true;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            if (_logger.IsDebug) _logger.Debug($"MCP token metadata for {token} failed: {e.Message}");
            return true;
        }
    }

    private bool IsHead(BlockParameter block)
    {
        if (block.Type == BlockParameterType.Latest)
        {
            return true;
        }

        BlockHeader? head = blockFinder?.Head?.Header;
        return head is not null && (block.BlockHash is { } hash ? hash == head.Hash : block.BlockNumber == head.Number);
    }

    /// <summary>Formats a raw integer amount with <paramref name="decimals"/> as a decimal string, such as <c>1.5</c>.</summary>
    /// <remarks>Exact at any size; trailing zeros are trimmed and a negative <paramref name="decimals"/> is treated as 0.</remarks>
    public static string FormatUnits(UInt256 amount, int decimals) => FormatUnits((BigInteger)amount, decimals);

    /// <summary>Formats a signed raw integer amount with <paramref name="decimals"/>, such as <c>-1.5</c>.</summary>
    public static string FormatUnits(BigInteger amount, int decimals)
    {
        bool negative = amount.Sign < 0;
        string digits = BigInteger.Abs(amount).ToString(CultureInfo.InvariantCulture);
        if (decimals > 0)
        {
            if (digits.Length <= decimals)
            {
                digits = new string('0', decimals - digits.Length + 1) + digits;
            }

            int point = digits.Length - decimals;
            ReadOnlySpan<char> fraction = digits.AsSpan(point).TrimEnd('0');
            digits = fraction.Length == 0 ? digits[..point] : string.Concat(digits.AsSpan(0, point), ".", fraction);
        }

        return negative ? "-" + digits : digits;
    }

    /// <summary>Formats a decimal integer string (as produced by <see cref="McpAbiCodec"/>) with <paramref name="decimals"/>, or returns <see langword="null"/> if it is not an integer.</summary>
    public static string? FormatUnits(string? decimalAmount, int decimals) =>
        decimalAmount is { Length: > 0 and <= 80 } && BigInteger.TryParse(decimalAmount, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out BigInteger value)
            ? FormatUnits(value, decimals)
            : null;

    /// <summary>Decodes a <c>name()</c>/<c>symbol()</c> result: an ABI <c>string</c> or a legacy zero-padded <c>bytes32</c>.</summary>
    /// <returns>The sanitized text, or <see langword="null"/> if the data is neither or the text is empty.</returns>
    public static string? DecodeText(ReadOnlySpan<byte> data)
    {
        string? text = null;
        if (data.Length == 32)
        {
            int length = data.IndexOf((byte)0);
            ReadOnlySpan<byte> bytes = length < 0 ? data : data[..length];
            if (bytes.Length > 0 && !data[bytes.Length..].ContainsAnyExcept((byte)0))
            {
                text = Encoding.UTF8.GetString(bytes);
            }
        }
        else if (McpAbiCodec.TryDecode(StringOutput, data, out object?[]? values, out _))
        {
            text = values[0] as string;
        }

        return text is null ? null : Sanitize(text);
    }

    /// <summary>
    /// Makes untrusted contract text safe to show (see <see cref="McpText"/>): no control, format, private-use or unassigned
    /// characters, no variation selectors, trimmed, at most <see cref="MaxTextLength"/> characters.
    /// </summary>
    /// <returns>The sanitized text, or <see langword="null"/> if nothing visible remains.</returns>
    public static string? Sanitize(string text)
    {
        // Trimmed before the cut, so leading blanks (such as control characters turned into spaces) take no room.
        string result = McpText.Sanitize(McpText.Sanitize(text, text.Length).Trim(), MaxTextLength).TrimEnd();
        return result.Length == 0 ? null : result;
    }

    /// <summary>Executes a bounded read-only call and classifies the outcome.</summary>
    /// <returns>The return data, or <see langword="null"/> on failure; <paramref name="transient"/> is set when the failure may go away on retry.</returns>
    internal static byte[]? Call(IEthRpcModule eth, Address to, byte[] input, BlockParameter block, ulong gas, ref bool transient)
    {
        LegacyTransactionForRpc transaction = new() { To = to, Input = input, Gas = gas };
        using ResultWrapper<HexBytes> result = eth.eth_call(transaction, block);
        if (result.Result.ResultType == ResultType.Success)
        {
            return result.Data.Bytes.ToArray();
        }

        // Reverts and EVM errors (-32000, such as out of gas) are the contract's behaviour; anything else may be temporary.
        if (result.IsTemporary || result.ErrorCode is not (ErrorCodes.ExecutionReverted or ErrorCodes.Default))
        {
            transient = true;
        }

        return null;
    }

    private static string? ReadText(IEthRpcModule eth, Address token, BlockParameter block, byte[] selector, ref bool transient) =>
        Call(eth, token, selector, block, MetadataCallGas, ref transient) is { } data ? DecodeText(data) : null;

    private static byte? ReadDecimals(IEthRpcModule eth, Address token, BlockParameter block, ref bool transient)
    {
        byte[]? data = Call(eth, token, DecimalsSelector, block, MetadataCallGas, ref transient);
        if (data is not { Length: >= 32 } || data.AsSpan(0, 31).ContainsAnyExcept((byte)0))
        {
            return null;
        }

        return data[31];
    }

    private void Add((ulong, AddressAsKey) key, McpTokenInfo info, long sequence, DateTimeOffset expires)
    {
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out LinkedListNode<CacheEntry>? existing))
            {
                // A read that started later saw state at least as new: an older read finishing last must not replace it.
                if (existing.Value.Sequence > sequence)
                {
                    return;
                }

                _order.Remove(existing);
            }

            _cache[key] = _order.AddFirst(new CacheEntry(key, info, expires, sequence));
            while (_cache.Count > CacheCapacity && _order.Last is { } last)
            {
                _order.RemoveLast();
                _cache.Remove(last.Value.Key);
            }
        }
    }

    private sealed record CacheEntry((ulong ChainId, AddressAsKey Address) Key, McpTokenInfo Info, DateTimeOffset Expires, long Sequence);
}
