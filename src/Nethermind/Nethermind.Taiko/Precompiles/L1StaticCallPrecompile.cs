// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
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
/// </summary>
/// <summary>
/// Gas model:
///   BaseGasCost  = 2000 (fixed)
///   DataGasCost  = 10000 (per-call overhead) + 16/byte (calldata) + actual L1 gas consumed
///   The L1 call is executed during DataGasCost() via debug_traceCall, with gas limit =
///   min(remaining L2 gas − overhead, configurable cap). Result is cached for Run() via [ThreadStatic].
/// </summary>
public class L1StaticCallPrecompile : IPrecompile<L1StaticCallPrecompile>
{
    public static readonly L1StaticCallPrecompile Instance = new();

    private const string L1StaticCallFailed = "l1 static call failed";

    /// <summary>
    /// Cached result from DataGasCost() for consumption by Run().
    /// Thread-safe: DataGasCost() and Run() execute sequentially on the same thread
    /// in VirtualMachine.RunPrecompile(). Cleared after Run() consumes it.
    /// </summary>
    [ThreadStatic]
    private static L1CallResult? s_cachedResult;

    private L1StaticCallPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x10002);
    public static string Name => "L1STATICCALL";
    public static IL1CallProvider? L1CallProvider { get; set; }
    public static ILogger Logger { get; set; }
    public static long GasCap { get; set; } = L1PrecompileConstants.L1CallDefaultGasCap;

    public long BaseGasCost(IReleaseSpec releaseSpec) => L1PrecompileConstants.L1StaticCallFixedGasCost;

    /// <summary>
    /// Executes the L1 call via debug_traceCall and returns static overhead + actual L1 gas consumed.
    /// The call result is cached in <see cref="s_cachedResult"/> for <see cref="Run"/> to return.
    /// The L1 gas limit is min(remaining L2 gas from <see cref="PrecompileGasContext"/>, <see cref="GasCap"/>).
    /// </summary>
    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length < L1PrecompileConstants.L1StaticCallMinInputLength)
            return 0L;

        int calldataLength = inputData.Length - L1PrecompileConstants.L1StaticCallMinInputLength;
        long staticCost = L1PrecompileConstants.L1StaticCallPerCallOverhead
            + L1PrecompileConstants.L1StaticCallPerByteCalldataCost * calldataLength;

        if (L1CallProvider is null)
        {
            s_cachedResult = L1CallResult.Failure();
            return staticCost;
        }

        Address contractAddress = new(inputData.Span[..Address.Size]);
        UInt256 blockNumber = new(inputData.Span[Address.Size..(Address.Size + L1PrecompileConstants.BlockNumberBytes)], isBigEndian: true);
        byte[] calldata = inputData.Span[(Address.Size + L1PrecompileConstants.BlockNumberBytes)..].ToArray();

        // Reserve base + static overhead so total precompile cost never exceeds available gas
        long overhead = L1PrecompileConstants.L1StaticCallFixedGasCost + staticCost;
        long affordableL1Gas = PrecompileGasContext.AvailableGas - overhead;
        long gasLimit = Math.Min(Math.Max(0, affordableL1Gas), GasCap);

        L1CallResult result;
        try
        {
            result = L1CallProvider.ExecuteTraceCall(contractAddress, blockNumber, calldata, gasLimit);
        }
        catch (Exception ex)
        {
            if (Logger.IsError) Logger.Error($"L1STATICCALL: exception in ExecuteTraceCall: {ex.Message}", ex);
            result = L1CallResult.Failure();
        }

        s_cachedResult = result;

        if (result.Failed || result.ReturnData is null)
            return staticCost;

        return staticCost + result.GasUsed;
    }

    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        L1PrecompileMetrics.L1StaticCallPrecompile++;

        // Consume cached result from DataGasCost() — clear to prevent stale data
        L1CallResult? cached = s_cachedResult;
        s_cachedResult = null;

        if (inputData.Length < L1PrecompileConstants.L1StaticCallMinInputLength)
        {
            if (Logger.IsWarn) Logger.Warn($"L1STATICCALL: rejected invalid input length {inputData.Length}, minimum {L1PrecompileConstants.L1StaticCallMinInputLength}");
            return Errors.InvalidInputLength;
        }

        if (cached is null || cached.Value.Failed || cached.Value.ReturnData is null)
        {
            if (Logger.IsWarn) Logger.Warn("L1STATICCALL: call failed or no cached result");
            return L1StaticCallFailed;
        }

        byte[] returnData = cached.Value.ReturnData;

        if (returnData.Length > L1PrecompileConstants.L1StaticCallMaxReturnDataSize)
        {
            if (Logger.IsWarn) Logger.Warn($"L1STATICCALL: return data too large ({returnData.Length} bytes, max {L1PrecompileConstants.L1StaticCallMaxReturnDataSize})");
            return L1StaticCallFailed;
        }

        if (Logger.IsDebug) Logger.Debug($"L1STATICCALL: success, return_len={returnData.Length}, l1GasUsed={cached.Value.GasUsed}");

        return returnData;
    }
}
