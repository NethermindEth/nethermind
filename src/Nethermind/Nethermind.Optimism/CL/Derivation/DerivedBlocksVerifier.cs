// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL.Derivation;

public class DerivedBlocksVerifier(ILogManager logManager) : IDerivedBlocksVerifier
{
    private readonly ILogger _logger = logManager.GetClassLogger();

    public bool ComparePayloadAttributes(OptimismPayloadAttributes expected, OptimismPayloadAttributes actual, ulong blockNumber)
    {
        bool result = true;
        if (expected.NoTxPool != actual.NoTxPool)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid NoTxPool. Expected {expected.NoTxPool}, Actual {actual.NoTxPool}");
            result = false;
        }

        if ((expected.EIP1559Params is null && actual.EIP1559Params is not null) ||
            (expected.EIP1559Params is not null && actual.EIP1559Params is null) ||
            (expected.EIP1559Params is not null && actual.EIP1559Params is not null &&
             !expected.EIP1559Params.SequenceEqual(actual.EIP1559Params)))
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid Eip1559Params expected: {expected.EIP1559Params?.ToHexString()}, actual: {actual.EIP1559Params?.ToHexString()}");
            result = false;
        }

        if (expected.GasLimit != actual.GasLimit)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid GasLimit. Expected {expected.GasLimit}, Actual {actual.GasLimit}");
            result = false;
        }

        if (expected.ParentBeaconBlockRoot != actual.ParentBeaconBlockRoot)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid ParentBeaconBlockRoot. Expected {expected.ParentBeaconBlockRoot}, Actual {actual.ParentBeaconBlockRoot}");
            result = false;
        }

        if (expected.PrevRandao != actual.PrevRandao)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid PrevRandao. Expected {expected.PrevRandao}, Actual {actual.PrevRandao}");
            result = false;
        }

        if (expected.SuggestedFeeRecipient != actual.SuggestedFeeRecipient)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid SuggestedFeeRecipient. Expected {expected.SuggestedFeeRecipient}, Actual {actual.SuggestedFeeRecipient}");
            result = false;
        }

        if (expected.Timestamp != actual.Timestamp)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid Timestamp. Expected {expected.Timestamp}, Actual {actual.Timestamp}");
            result = false;
        }

        if (expected.Withdrawals != actual.Withdrawals)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid Withdrawals");
            result = false;
        }

        if (expected.Transactions!.Length != actual.Transactions!.Length)
        {
            if (_logger.IsWarn) _logger.Warn($"Invalid Transactions.Length. Expected {expected.Transactions!.Length}, Actual {actual.Transactions!.Length}");
            result = false;
        }
        else
        {
            for (int i = 0; i < expected.Transactions.Length; ++i)
            {
                if (!expected.Transactions[i].SequenceEqual(actual.Transactions[i]))
                {
                    Transaction actualDecoded = Rlp.Decode<Transaction>(actual.Transactions[i], RlpBehaviors.SkipTypedWrapping);
                    Transaction expectedDecoded = Rlp.Decode<Transaction>(expected.Transactions[i], RlpBehaviors.SkipTypedWrapping);
                    if (_logger.IsWarn) _logger.Warn($"Invalid transaction. Index {i}. Block {blockNumber}");
                    if (_logger.IsWarn) _logger.Warn($"Expected: {expectedDecoded}");
                    if (_logger.IsWarn) _logger.Warn($"Actual: {actualDecoded}");
                    result = false;
                }
            }
        }
        return result;
    }
}
