// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;

namespace Nethermind.Optimism.CL.Derivation;

public class DerivedBlocksVerifier : IDerivedBlocksVerifier
{
    private readonly ILogger _logger;

    public DerivedBlocksVerifier(ILogger logger)
    {
        _logger = logger;
    }

    public void ComparePayloadAttributes(OptimismPayloadAttributes expected, OptimismPayloadAttributes actual, ulong blockNumber)
    {
        if (expected.NoTxPool != actual.NoTxPool)
        {
            _logger.Error($"Invalid NoTxPool. Expected {expected.NoTxPool}, Actual {actual.NoTxPool}");
        }

        if ((expected.EIP1559Params is null && actual.EIP1559Params is not null) ||
            (expected.EIP1559Params is not null && actual.EIP1559Params is null) ||
            (expected.EIP1559Params is not null && actual.EIP1559Params is not null &&
             !expected.EIP1559Params.SequenceEqual(actual.EIP1559Params)))
        {
            _logger.Error($"Invalid Eip1559Params expected: {expected.EIP1559Params?.ToHexString()}, actual: {actual.EIP1559Params?.ToHexString()}");
        }

        if (expected.GasLimit != actual.GasLimit)
        {
            _logger.Error($"Invalid GasLimit. Expected {expected.GasLimit}, Actual {actual.GasLimit}");
        }

        if (expected.ParentBeaconBlockRoot != actual.ParentBeaconBlockRoot)
        {
            _logger.Error($"Invalid ParentBeaconBlockRoot. Expected {expected.ParentBeaconBlockRoot}, Actual {actual.ParentBeaconBlockRoot}");
        }

        if (expected.PrevRandao != actual.PrevRandao)
        {
            _logger.Error($"Invalid PrevRandao. Expected {expected.PrevRandao}, Actual {actual.PrevRandao}");
        }

        if (expected.SuggestedFeeRecipient != actual.SuggestedFeeRecipient)
        {
            _logger.Error($"Invalid SuggestedFeeRecipient. Expected {expected.SuggestedFeeRecipient}, Actual {actual.SuggestedFeeRecipient}");
        }

        if (expected.Timestamp != actual.Timestamp)
        {
            _logger.Error($"Invalid Timestamp. Expected {expected.Timestamp}, Actual {actual.Timestamp}");
        }

        if (expected.Withdrawals != actual.Withdrawals)
        {
            _logger.Error($"Invalid Withdrawals");
        }

        if (expected.Transactions!.Length != actual.Transactions!.Length)
        {
            _logger.Error($"Invalid Transactions.Length. Expected {expected.Transactions!.Length}, Actual {actual.Transactions!.Length}");
        }
        else
        {
            // for (int i = 0; i < expected.Transactions.Length; ++i)
            // {
            //     if (!expected.Transactions[i].SequenceEqual(actual.Transactions[i]))
            //     {
            //         _logger.Error($"Invalid transaction");
            //         _logger.Error($"Expected: {expected.Transactions[i].ToHexString()}");
            //         _logger.Error($"Actual: {actual.Transactions[i].ToHexString()}");
            //     }
            // }
        }
    }
}
