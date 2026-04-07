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
public class L1StaticCallPrecompile : IPrecompile<L1StaticCallPrecompile>
{
    public static readonly L1StaticCallPrecompile Instance = new();

    private const string L1StaticCallFailed = "l1 static call failed";

    private L1StaticCallPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x10002);
    public static string Name => "L1STATICCALL";
    public static IL1CallProvider? L1CallProvider { get; set; }
    public static ILogger Logger { get; set; }

    public long BaseGasCost(IReleaseSpec releaseSpec) => L1PrecompileConstants.L1StaticCallFixedGasCost;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        if (inputData.Length < L1PrecompileConstants.L1StaticCallMinInputLength)
            return 0L;

        int calldataLength = inputData.Length - L1PrecompileConstants.L1StaticCallMinInputLength;
        return L1PrecompileConstants.L1StaticCallPerCallOverhead
            + L1PrecompileConstants.L1StaticCallPerByteCalldataCost * calldataLength;
    }

    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        L1PrecompileMetrics.L1StaticCallPrecompile++;
        if (Logger.IsDebug) Logger.Debug($"L1STATICCALL: precompile called, input_len={inputData.Length}");

        if (inputData.Length < L1PrecompileConstants.L1StaticCallMinInputLength)
        {
            if (Logger.IsWarn) Logger.Warn($"L1STATICCALL: rejected invalid input length {inputData.Length}, minimum {L1PrecompileConstants.L1StaticCallMinInputLength}");
            return Errors.InvalidInputLength;
        }

        Address contractAddress = new(inputData.Span[..Address.Size]);
        UInt256 blockNumber = new(inputData.Span[Address.Size..(Address.Size + L1PrecompileConstants.BlockNumberBytes)], isBigEndian: true);
        byte[] calldata = inputData.Span[(Address.Size + L1PrecompileConstants.BlockNumberBytes)..].ToArray();

        if (Logger.IsDebug) Logger.Debug($"L1STATICCALL: request contract={contractAddress}, block={blockNumber}, calldata_len={calldata.Length}");

        byte[]? result = ExecuteL1StaticCall(contractAddress, blockNumber, calldata);
        if (result is null)
        {
            if (Logger.IsWarn) Logger.Warn($"L1STATICCALL: call returned null for contract={contractAddress}, block={blockNumber}");
            return L1StaticCallFailed;
        }

        if (result.Length > L1PrecompileConstants.L1StaticCallMaxReturnDataSize)
        {
            if (Logger.IsWarn) Logger.Warn($"L1STATICCALL: return data too large ({result.Length} bytes, max {L1PrecompileConstants.L1StaticCallMaxReturnDataSize})");
            return L1StaticCallFailed;
        }

        if (Logger.IsDebug) Logger.Debug($"L1STATICCALL: success contract={contractAddress}, block={blockNumber}, return_len={result.Length}");

        return result;
    }

    private byte[]? ExecuteL1StaticCall(Address contractAddress, UInt256 blockNumber, byte[] calldata)
    {
        try
        {
            byte[]? result = L1CallProvider?.ExecuteStaticCall(contractAddress, blockNumber, calldata);
            if (Logger.IsTrace) Logger.Trace($"L1STATICCALL: provider returned {(result is null ? "null" : $"{result.Length} bytes")}");
            return result;
        }
        catch (Exception ex)
        {
            if (Logger.IsError) Logger.Error($"L1STATICCALL: exception in ExecuteStaticCall: {ex.Message}", ex);
            return null;
        }
    }
}
