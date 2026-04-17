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
/// L1SLOAD precompile - read storage values from L1 contracts (RIP-7728).
///
/// Input layout:
///   [0:20)   address      — L1 contract address
///   [20:52)  storageKey   — storage slot to read
///   [52:84)  blockNumber  — L1 block number
///
/// Output: 32-byte storage value.
/// </summary>
public class L1SloadPrecompile : IPrecompile<L1SloadPrecompile>
{
    public static readonly L1SloadPrecompile Instance = new();

    private const string L1StorageAccessFailed = "l1 storage access failed";

    private L1SloadPrecompile()
    {
    }

    public static Address Address { get; } = Address.FromNumber(0x10001);
    public static string Name => "L1SLOAD";

    // L1SLOAD calls L1 via RPC — results depend on L1 state and must not be cached.
    public bool SupportsCaching => false;
    public static IL1StorageProvider? L1StorageProvider { get; set; }
    public static ILogger Logger { get; set; }

    public long BaseGasCost(IReleaseSpec releaseSpec) => L1PrecompileConstants.L1SloadFixedGasCost;

    public long DataGasCost(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec) =>
        inputData.Length != L1PrecompileConstants.L1SloadExpectedInputLength ? 0L : L1PrecompileConstants.L1SloadPerLoadGasCost;

    public Result<byte[]> Run(ReadOnlyMemory<byte> inputData, IReleaseSpec releaseSpec)
    {
        L1PrecompileMetrics.L1SloadPrecompile++;
        if (Logger.IsDebug) Logger.Debug($"L1SLOAD: precompile called, input_len={inputData.Length}");

        if (inputData.Length != L1PrecompileConstants.L1SloadExpectedInputLength)
        {
            if (Logger.IsWarn) Logger.Warn($"L1SLOAD: rejected invalid input length {inputData.Length}, expected {L1PrecompileConstants.L1SloadExpectedInputLength}");
            return Errors.InvalidInputLength;
        }

        Address contractAddress = new(inputData.Span[..Address.Size]);
        UInt256 storageKey = new(inputData.Span[Address.Size..(Address.Size + L1PrecompileConstants.L1SloadStorageKeyBytes)], isBigEndian: true);
        UInt256 blockNumber = new(inputData.Span[(Address.Size + L1PrecompileConstants.L1SloadStorageKeyBytes)..], isBigEndian: true);

        if (Logger.IsDebug) Logger.Debug($"L1SLOAD: request contract={contractAddress}, key={storageKey}, block={blockNumber}");

        UInt256? storageValue = GetL1StorageValue(contractAddress, blockNumber, storageKey);
        if (storageValue is null)
        {
            if (Logger.IsWarn) Logger.Warn($"L1SLOAD: storage access returned null for contract={contractAddress}, key={storageKey}, block={blockNumber}");
            return L1StorageAccessFailed;
        }

        if (Logger.IsDebug) Logger.Debug($"L1SLOAD: success contract={contractAddress}, key={storageKey}, block={blockNumber}, value={storageValue.Value}");

        byte[] output = new byte[32];
        storageValue.Value.ToBigEndian().CopyTo(output.AsSpan());

        return output;
    }

    private UInt256? GetL1StorageValue(Address contractAddress, UInt256 blockNumber, UInt256 storageKey)
    {
        try
        {
            UInt256? result = L1StorageProvider?.GetStorageValue(contractAddress, blockNumber, storageKey);
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
