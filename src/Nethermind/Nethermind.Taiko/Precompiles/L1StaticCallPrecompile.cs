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
/// L1STATICCALL precompile - execute arbitrary read-only calls against L1 contracts.
///
/// Input layout:
///   [0:20)   address      — L1 contract address to call
///   [20:52)  blockNumber  — L1 block number
///   [52:...)  calldata    — ABI-encoded function call (may be empty)
///
/// Output: variable-length ABI-encoded return data from the L1 call.
///
/// Gas model:
///   BaseGasCost  = 2000 (fixed)
///   DataGasCost  = 10000 (per-call overhead) + 16/byte (calldata)
///   Dynamic cost = actual L1 gas consumed (reported via IContextAwarePrecompile.Run)
///   The L1 call is executed during Run() via debug_traceCall, with gas limit =
///   min(remainingGas, GasCap).
/// </summary>
public class L1StaticCallPrecompile : IPrecompile<L1StaticCallPrecompile>, IContextAwarePrecompile
{
    public static readonly L1StaticCallPrecompile Instance = new();
    static L1StaticCallPrecompile IPrecompile<L1StaticCallPrecompile>.Instance => Instance;

    private const string L1StaticCallFailed = "l1 static call failed";
    private const string BlockOutOfRange = "l1 block out of 256-block lookback range";

    private L1StaticCallPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x10002);
    public static string Name => "L1STATICCALL";

    // L1STATICCALL calls L1 via RPC — results depend on L1 state and must not be cached.
    // Caching also wraps the precompile in CachedPrecompile which strips IContextAwarePrecompile.
    public bool SupportsCaching => false;
    public static IL1CallProvider? L1CallProvider { get; set; }
    public static ILogger Logger { get; set; }
    public static ulong GasCap => (ulong)L1PrecompileConstants.L1CallMaxGasCap;

    public ulong BaseGasCost(IReleaseSpec releaseSpec) => L1PrecompileConstants.L1StaticCallFixedGasCost;

    /// <summary>
    /// Returns the static overhead cost: per-call overhead + per-byte calldata cost.
    /// The dynamic L1 gas cost is handled by <see cref="Run(ReadOnlyMemory{byte}, IReleaseSpec, in PrecompileExtras)"/>.
    /// </summary>
    public ulong DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length < L1PrecompileConstants.L1StaticCallMinInputLength)
            return 0UL;

        int calldataLength = inputData.Length - L1PrecompileConstants.L1StaticCallMinInputLength;
        return L1PrecompileConstants.L1StaticCallPerCallOverhead
            + L1PrecompileConstants.L1StaticCallPerByteCalldataCost * (ulong)calldataLength;
    }

    /// <summary>
    /// Context-aware execution. The Taiko VM dispatches here during normal block processing,
    /// supplying remaining gas (used to clamp the L1 call gas limit) and the cached L1 origin
    /// (used for the 256-block lookback range check).
    /// </summary>
    public Result<(byte[] returnValue, ulong gasConsumed)> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec, in PrecompileExtras extras)
    {
        L1PrecompileMetrics.L1StaticCallPrecompile++;
        if (Logger.IsDebug) Logger.Debug($"L1STATICCALL: precompile called, input_len={inputData.Length}, remainingGas={extras.RemainingGas}");

        if (inputData.Length < L1PrecompileConstants.L1StaticCallMinInputLength)
        {
            if (Logger.IsWarn) Logger.Warn($"L1STATICCALL: rejected invalid input length {inputData.Length}, minimum {L1PrecompileConstants.L1StaticCallMinInputLength}");
            return Result<(byte[] returnValue, ulong gasConsumed)>.Fail(Errors.InvalidInputLength);
        }

        if (L1CallProvider is null)
        {
            if (Logger.IsWarn) Logger.Warn("L1STATICCALL: no L1CallProvider configured");
            return Result<(byte[] returnValue, ulong gasConsumed)>.Fail(L1StaticCallFailed);
        }

        Address contractAddress = new(inputData.Span[..Address.Size]);
        UInt256 blockNumber = new(inputData.Span[Address.Size..(Address.Size + L1PrecompileConstants.BlockNumberBytes)], isBigEndian: true);
        byte[] calldata = inputData.Span[(Address.Size + L1PrecompileConstants.BlockNumberBytes)..].ToArray();

        // Range validation: only when an L1 origin is available. null = preconf block / eth_call /
        // tooling path with no origin → permissive (the proving layer enforces correctness instead).
        if (extras.L1Origin is { } origin && !L1PrecompileConstants.IsBlockInRange(blockNumber, origin))
        {
            if (Logger.IsWarn) Logger.Warn($"L1STATICCALL: block {blockNumber} outside [{origin}-{L1PrecompileConstants.MaxBlockLookback}, {origin}]");
            return Result<(byte[] returnValue, ulong gasConsumed)>.Fail(BlockOutOfRange);
        }

        ulong gasLimit = Math.Min(extras.RemainingGas, GasCap);
        if (Logger.IsDebug) Logger.Debug($"L1STATICCALL: request contract={contractAddress}, block={blockNumber}, calldata_len={calldata.Length}, gasLimit={gasLimit}");

        L1CallResult result;
        try
        {
            result = L1CallProvider.ExecuteTraceCall(contractAddress, blockNumber, calldata, gasLimit);
        }
        catch (Exception ex)
        {
            if (Logger.IsError) Logger.Error($"L1STATICCALL: exception in ExecuteTraceCall: {ex.Message}", ex);
            return Result<(byte[] returnValue, ulong gasConsumed)>.Fail(L1StaticCallFailed);
        }

        if (result.Failed || result.ReturnData is null)
        {
            if (Logger.IsWarn) Logger.Warn("L1STATICCALL: L1 call failed");
            // Report gasUsed even on failure — the L1 node did the work and the user must pay.
            // On L1 OOG, gasUsed equals the full gas limit.
            return Result<(byte[] returnValue, ulong gasConsumed)>.Fail(
                L1StaticCallFailed, (Array.Empty<byte>(), result.GasUsed));
        }

        if (result.ReturnData.Length > L1PrecompileConstants.L1StaticCallMaxReturnDataSize)
        {
            if (Logger.IsWarn) Logger.Warn($"L1STATICCALL: return data too large ({result.ReturnData.Length} bytes, max {L1PrecompileConstants.L1StaticCallMaxReturnDataSize})");
            return Result<(byte[] returnValue, ulong gasConsumed)>.Fail(
                L1StaticCallFailed, (Array.Empty<byte>(), result.GasUsed));
        }

        if (Logger.IsDebug) Logger.Debug($"L1STATICCALL: success, return_len={result.ReturnData.Length}, l1GasUsed={result.GasUsed}");

        return (result.ReturnData, result.GasUsed);
    }

    /// <summary>
    /// Non-context-aware fallback. Used by callers outside the Taiko VM (caching layer, tooling)
    /// that don't have execution-context extras to pass. Uses <see cref="GasCap"/> as the L1 gas
    /// limit and treats the lookback check as permissive (no origin available).
    /// </summary>
    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        PrecompileExtras extras = new(remainingGas: GasCap);
        Result<(byte[] returnValue, ulong gasConsumed)> result = Run(inputData, releaseSpec, in extras);
        // Implicit string→Result<byte[]> conversion fills Data with Array.Empty<byte>() on failure,
        // which the IPrecompile contract expects (callers deconstruct result and assert .IsEmpty).
        return result ? Result<byte[]>.Success(result.Data.returnValue) : result.Error!;
    }
}
