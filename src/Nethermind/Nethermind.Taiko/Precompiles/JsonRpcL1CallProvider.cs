// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Int256;
using Nethermind.JsonRpc.Client;
using Nethermind.Logging;

namespace Nethermind.Taiko.Precompiles;

/// <summary>
/// Does not own <paramref name="rpcClient"/> — caller (TaikoPlugin) manages its lifetime.
/// </summary>
public class JsonRpcL1CallProvider(IJsonRpcClient rpcClient, ILogManager logManager) : IL1CallProvider
{
    private readonly ILogger _logger = logManager.GetClassLogger<JsonRpcL1CallProvider>();

    public L1CallResult ExecuteTraceCall(Address contractAddress, UInt256 blockNumber, byte[] calldata, long gasLimit)
    {
        try
        {
            string calldataHex = calldata.ToHexString(withZeroX: true);
            string gasHex = gasLimit.ToHexString(true);
            string blockHex = blockNumber.ToHexString(true);

            if (_logger.IsDebug) _logger.Debug($"L1STATICCALL: sending debug_traceCall — contract={contractAddress}, calldata_len={calldata.Length}, block={blockHex}, gasLimit={gasLimit}");

            // Sync-over-async: IPrecompile.Run() is synchronous by design.
            // Acceptable for devnet; async precompile pipeline needed for production load.
            DebugTraceCallResult? response = rpcClient.Post<DebugTraceCallResult>("debug_traceCall", new object[]
            {
                new { from = Address.Zero.ToString(), to = contractAddress.ToString(), data = calldataHex, gas = gasHex },
                blockHex,
                new { } // default tracer options
            }).GetAwaiter().GetResult();

            if (response is null)
            {
                if (_logger.IsWarn) _logger.Warn($"L1STATICCALL: debug_traceCall returned null — contract={contractAddress}, block={blockHex}");
                return L1CallResult.Failure();
            }

            // Clamp to gasLimit — the L1 node must not cause us to charge more than we budgeted.
            long gasUsed = Math.Min(response.Gas, gasLimit);

            if (response.Failed)
            {
                if (_logger.IsWarn) _logger.Warn($"L1STATICCALL: L1 call failed — contract={contractAddress}, block={blockHex}, gasUsed={gasUsed}");
                return new L1CallResult(null, gasUsed, true);
            }

            byte[] returnData = Convert.FromHexString(
                response.ReturnValue.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? response.ReturnValue[2..]
                    : response.ReturnValue);

            if (_logger.IsDebug) _logger.Debug($"L1STATICCALL: debug_traceCall success — contract={contractAddress}, block={blockHex}, gasUsed={gasUsed}, return_len={returnData.Length}");
            return new L1CallResult(returnData, gasUsed, false);
        }
        catch (Exception ex)
        {
            if (_logger.IsError) _logger.Error($"L1STATICCALL: debug_traceCall exception — contract={contractAddress}, block={blockNumber.ToHexString(true)}, error={ex}");
            return L1CallResult.Failure();
        }
    }
}
