// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Logging;
using Nethermind.Optimism.Rpc;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Optimism.CL.Derivation;

public class DerivedBlocksVerifier : IDerivedBlocksVerifier
{
    private readonly ILogger _logger;

    public DerivedBlocksVerifier(ILogger logger)
    {
        _logger = logger;
    }

    public bool ComparePayloadAttributes(OptimismPayloadAttributes expected, OptimismPayloadAttributes actual, ulong blockNumber)
    {
        bool result = true;
        if (expected.NoTxPool != actual.NoTxPool)
        {
            _logger.Error($"Invalid NoTxPool. Expected {expected.NoTxPool}, Actual {actual.NoTxPool}");
            result = false;
        }

        if ((expected.EIP1559Params is null && actual.EIP1559Params is not null) ||
            (expected.EIP1559Params is not null && actual.EIP1559Params is null) ||
            (expected.EIP1559Params is not null && actual.EIP1559Params is not null &&
             !expected.EIP1559Params.SequenceEqual(actual.EIP1559Params)))
        {
            _logger.Error($"Invalid Eip1559Params expected: {expected.EIP1559Params?.ToHexString()}, actual: {actual.EIP1559Params?.ToHexString()}");
            result = false;
        }

        if (expected.GasLimit != actual.GasLimit)
        {
            _logger.Error($"Invalid GasLimit. Expected {expected.GasLimit}, Actual {actual.GasLimit}");
            result = false;
        }

        if (expected.ParentBeaconBlockRoot != actual.ParentBeaconBlockRoot)
        {
            _logger.Error($"Invalid ParentBeaconBlockRoot. Expected {expected.ParentBeaconBlockRoot}, Actual {actual.ParentBeaconBlockRoot}");
            result = false;
        }

        if (expected.PrevRandao != actual.PrevRandao)
        {
            _logger.Error($"Invalid PrevRandao. Expected {expected.PrevRandao}, Actual {actual.PrevRandao}");
            result = false;
        }

        if (expected.SuggestedFeeRecipient != actual.SuggestedFeeRecipient)
        {
            _logger.Error($"Invalid SuggestedFeeRecipient. Expected {expected.SuggestedFeeRecipient}, Actual {actual.SuggestedFeeRecipient}");
            result = false;
        }

        if (expected.Timestamp != actual.Timestamp)
        {
            _logger.Error($"Invalid Timestamp. Expected {expected.Timestamp}, Actual {actual.Timestamp}");
            result = false;
        }

        if (expected.Withdrawals != actual.Withdrawals)
        {
            _logger.Error($"Invalid Withdrawals");
            result = false;
        }

        if (expected.Transactions!.Length != actual.Transactions!.Length)
        {
            _logger.Error($"Invalid Transactions.Length. Expected {expected.Transactions!.Length}, Actual {actual.Transactions!.Length}");
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
                    _logger.Error($"Invalid transaction. Index {i}. Block {blockNumber}");
                    _logger.Error($"Expected: {expectedDecoded}");
                    _logger.Error($"Actual: {actualDecoded}");
                    result = false;
                }
            }
        }
        return result;
    }
}
