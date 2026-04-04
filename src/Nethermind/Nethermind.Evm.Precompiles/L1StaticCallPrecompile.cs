// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Evm.Precompiles;

/// <summary>
/// L1STATICCALL precompile - execute arbitrary read-only calls against L1 contracts.
///
/// The input to the L1STATICCALL precompile consists of:
///
/// | Byte range          | Name               | Description                                  |
/// | ------------------  | ------------------ | -------------------------------------------- |
/// | [0: 19] (20 bytes)  | target             | The L1 contract address to call               |
/// | [20: 51] (32 bytes) | blockNumber        | The L1 block number to call at                |
/// | [52: ...)  (variable)| calldata          | ABI-encoded function call (may be empty)      |
///
/// Output:
/// - Variable-length bytes (ABI-encoded return data from the L1 call)
/// </summary>
public class L1StaticCallPrecompile : IPrecompile<L1StaticCallPrecompile>
{
    public static readonly L1StaticCallPrecompile Instance = new();

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
        Metrics.L1StaticCallPrecompile++;
        if (Logger.IsDebug) Logger.Debug($"L1STATICCALL: precompile called, input_len={inputData.Length}");

        if (inputData.Length < L1PrecompileConstants.L1StaticCallMinInputLength)
        {
            if (Logger.IsWarn) Logger.Warn($"L1STATICCALL: rejected invalid input length {inputData.Length}, minimum {L1PrecompileConstants.L1StaticCallMinInputLength}");
            return Errors.InvalidInputLength;
        }

        Address target = new(inputData.Span[..L1PrecompileConstants.AddressBytes]);
        UInt256 blockNumber = new(inputData.Span[L1PrecompileConstants.AddressBytes..(L1PrecompileConstants.AddressBytes + L1PrecompileConstants.BlockNumberBytes)], isBigEndian: true);
        byte[] calldata = inputData.Span[(L1PrecompileConstants.AddressBytes + L1PrecompileConstants.BlockNumberBytes)..].ToArray();

        if (Logger.IsDebug) Logger.Debug($"L1STATICCALL: request target={target}, block={blockNumber}, calldata_len={calldata.Length}");

        byte[]? result = ExecuteL1StaticCall(target, blockNumber, calldata);
        if (result is null)
        {
            if (Logger.IsWarn) Logger.Warn($"L1STATICCALL: call returned null for target={target}, block={blockNumber}");
            return Errors.L1CallFailed;
        }

        if (result.Length > L1PrecompileConstants.L1StaticCallMaxReturnDataSize)
        {
            if (Logger.IsWarn) Logger.Warn($"L1STATICCALL: return data too large ({result.Length} bytes, max {L1PrecompileConstants.L1StaticCallMaxReturnDataSize})");
            return Errors.L1CallFailed;
        }

        if (Logger.IsDebug) Logger.Debug($"L1STATICCALL: success target={target}, block={blockNumber}, return_len={result.Length}");

        return result;
    }

    private byte[]? ExecuteL1StaticCall(Address target, UInt256 blockNumber, byte[] calldata)
    {
        try
        {
            byte[]? result = L1CallProvider?.ExecuteStaticCall(target, blockNumber, calldata);
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
