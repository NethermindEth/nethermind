// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain.Find;
using Nethermind.Blockchain.Tracing.GethStyle;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.JsonRpc;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public partial class DebugRpcModuleTests
{
    /// <summary>
    /// Regression test for #12723: under EIP-7928 the block-access-list pool's per-worker adapters must honour the
    /// debug tracer's runtime Execute↔Trace swap. A <c>debug_traceCall</c> from a sender that has deployed code is
    /// rejected by Execute (EIP-3607, "sender has deployed code") but allowed by Trace, which skips sender
    /// validation; a BAL worker stuck on Execute fails the trace on the sender-code check.
    /// </summary>
    [Test]
    public async Task Debug_traceCall_from_contract_sender_traces_on_bal_path()
    {
        // Amsterdam activates both EIP-7928 (the BAL pool) and EIP-3607 (reject contract senders).
        Assert.That(Amsterdam.Instance.IsEip7928Enabled && Amsterdam.Instance.IsEip3607Enabled, Is.True);
        TestSpecProvider specProvider = new(Amsterdam.Instance) { AllowTestChainOverride = false };
        using Context ctx = await Context.Create(specProvider);

        LegacyTransactionForRpc call = new()
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB,
            Value = 0,
            Gas = 100_000,
            GasPrice = UInt256.Zero,
        };
        // A non-default tracer forces the buffered (eager) path so the trace actually runs the transaction; the
        // default struct-log tracer streams lazily and would never execute it. The sender is given deployed code:
        // Execute rejects it (EIP-3607), Trace skips that check.
        GethTraceOptions options = new()
        {
            Tracer = "callTracer",
            StateOverrides = new Dictionary<Address, AccountOverride>
            {
                { TestItem.AddressA, new AccountOverride { Code = Bytes.FromHexString("0x00") } }
            }
        };

        ResultWrapper<GethLikeTxTrace> result = ctx.DebugRpcModule.debug_traceCall(call, BlockParameter.Latest, options);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), () => result.Result.Error ?? string.Empty);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Failed, Is.False, "the contract-sender call must trace under skipped validation");
    }
}
