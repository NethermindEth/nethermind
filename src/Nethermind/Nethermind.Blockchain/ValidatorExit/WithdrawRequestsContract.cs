// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Reflection.Metadata;
using Nethermind.Blockchain.Contracts;
using Nethermind.Core.Specs;
using Nethermind.Evm;
using Nethermind.Evm.Tracing;
using Nethermind.State;

namespace Nethermind.Blockchain.ValidatorExit;

public class WithdrawRequestsContract : Contract
{
    ValidatorExit[] CalculateValidatorExits(IReleaseSpec spec, IWorldState state)
    {
        CallOutputTracer tracer = new();

        try
        {
            _transactionProcessor.Execute(transaction, new BlockExecutionContext(header), tracer);
            result = tracer.ReturnValue;
            return tracer.StatusCode == StatusCode.Success;
        }
        catch (Exception)
        {
            result = null;
            return false;
        }
    }
}
