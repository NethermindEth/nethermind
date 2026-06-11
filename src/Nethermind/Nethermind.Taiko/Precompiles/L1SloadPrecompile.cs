// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Evm.Precompiles;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// L1SLOAD precompile - read storage values from L1 contracts (RIP-7728).
///
/// Input layout:
///   [0:20)   address      — L1 contract address
///   [20:52)  storageKey   — storage slot to read
///   [52:84)  blockNumber  — L1 block number
///
/// Output: 32-byte storage value.
/// </summary>
public class L1SloadPrecompile : IPrecompile<L1SloadPrecompile>, IContextAwarePrecompile
{
    private const string L1StorageAccessFailed = "l1 storage access failed";
    private const string BlockOutOfRange = "l1 block out of 256-block lookback range";

    public static L1SloadPrecompile Instance { get; } = new();

    private L1SloadPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x10001);
    public static string Name => "L1SLOAD";

    // L1SLOAD calls L1 via RPC — results depend on L1 state and must not be cached.
    public bool SupportsCaching => false;
    public static IL1StorageProvider? L1StorageProvider { get; set; }
    public static ILogger Logger { get; set; } = NullLogger.Instance;

    public ulong BaseGasCost(IReleaseSpec releaseSpec) => L1PrecompileConstants.L1SloadFixedGasCost;

    public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        inputData.Length != L1PrecompileConstants.L1SloadExpectedInputLength ? 0UL : L1PrecompileConstants.L1SloadPerLoadGasCost;

    /// <summary>
    /// Non-context-aware fallback. Used by callers outside the Taiko VM (caching layer, tooling)
    /// that don't have an L1 origin to pass. Equivalent to calling the context-aware overload with
    /// <see cref="PrecompileExtras.None"/> (permissive).
    /// </summary>
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        Result<(byte[] returnValue, ulong gasConsumed)> result = Run(inputData, releaseSpec, in PrecompileExtras.None);
        // Implicit string→Result<byte[]> conversion fills Data with Array.Empty<byte>() on failure,
        // which the IPrecompile contract expects (callers deconstruct result and assert .IsEmpty).
        return result ? Result<byte[]>.Success(result.Data.returnValue) : result.Error!;
    }

    public Result<(byte[] returnValue, ulong gasConsumed)> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, in PrecompileExtras extras)
    {
        L1PrecompileMetrics.L1SloadPrecompile++;
        if (Logger.IsDebug) Logger.Debug($"L1SLOAD: precompile called, input_len={inputData.Length}");

        if (inputData.Length != L1PrecompileConstants.L1SloadExpectedInputLength)
        {
            if (Logger.IsWarn) Logger.Warn($"L1SLOAD: rejected invalid input length {inputData.Length}, expected {L1PrecompileConstants.L1SloadExpectedInputLength}");
            return Result<(byte[] returnValue, ulong gasConsumed)>.Fail(Errors.InvalidInputLength);
        }

        if (L1StorageProvider is null)
        {
            if (Logger.IsWarn) Logger.Warn("L1SLOAD: no L1StorageProvider configured");
            return Result<(byte[] returnValue, ulong gasConsumed)>.Fail(L1StorageAccessFailed);
        }

        Address contractAddress = new(inputData.Span[..Address.Size]);
        UInt256 storageKey = new(inputData.Span[Address.Size..(Address.Size + L1PrecompileConstants.L1SloadStorageKeyBytes)], isBigEndian: true);
        UInt256 blockNumber = new(inputData.Span[(Address.Size + L1PrecompileConstants.L1SloadStorageKeyBytes)..], isBigEndian: true);

        // Range validation: only when an L1 origin is available. null = preconf block / eth_call /
        // tooling path with no origin → permissive (the proving layer enforces correctness instead).
        if (extras.L1Origin is { } origin && !L1PrecompileConstants.IsBlockInRange(blockNumber, origin))
        {
            if (Logger.IsWarn) Logger.Warn($"L1SLOAD: block {blockNumber} outside [{origin}-{L1PrecompileConstants.MaxBlockLookback}, {origin}]");
            return Result<(byte[] returnValue, ulong gasConsumed)>.Fail(BlockOutOfRange);
        }

        if (Logger.IsDebug) Logger.Debug($"L1SLOAD: request contract={contractAddress}, key={storageKey}, block={blockNumber}");

        UInt256? storageValue = GetL1StorageValue(contractAddress, blockNumber, storageKey);
        if (storageValue is null)
        {
            if (Logger.IsWarn) Logger.Warn($"L1SLOAD: storage access returned null for contract={contractAddress}, key={storageKey}, block={blockNumber}");
            return Result<(byte[] returnValue, ulong gasConsumed)>.Fail(L1StorageAccessFailed);
        }

        if (Logger.IsDebug) Logger.Debug($"L1SLOAD: success contract={contractAddress}, key={storageKey}, block={blockNumber}, value={storageValue.Value}");

        byte[] output = new byte[32];
        storageValue.Value.ToBigEndian().CopyTo(output.AsSpan());

        return (output, 0UL);
    }

    private UInt256? GetL1StorageValue(Address contractAddress, UInt256 blockNumber, UInt256 storageKey)
    {
        try
        {
            UInt256? result = L1StorageProvider!.GetStorageValue(contractAddress, blockNumber, storageKey);
            if (Logger.IsTrace) Logger.Trace($"L1SLOAD: provider returned {(result is null ? "null" : result.Value.ToString())}");
            return result;
        }
        catch (Exception ex)
        {
            if (Logger.IsError) Logger.Error($"L1SLOAD: exception in GetL1StorageValue: {ex.Message}", ex);
            return null;
        }
    }
}
