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
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules;

public class DebugTraceCallBalTests
{
    /// <summary>Builds a chain on the Amsterdam fork with the EIP-7928 block-access-list path active and EIP-3607 on.</summary>
    private static async Task<TestRpcBlockchain> BuildAmsterdamBalChain()
    {
        OverridableReleaseSpec spec = new(Amsterdam.Instance) { IsEip3607Enabled = true };
        TestSpecProvider specProvider = new(spec) { AllowTestChainOverride = false };
        // Pin the EIP-7928 premise so a spec change can't silently turn this into a non-BAL test.
        Assert.That(spec.IsEip7928Enabled, Is.True);
        return await TestRpcBlockchain.ForTest(new TestRpcBlockchain()).Build(specProvider);
    }

    /// <summary>
    /// Regression test for #12723: under EIP-7928 the block-access-list pool's per-worker adapters must honour
    /// the debug tracer's runtime Execute↔Trace swap. A <c>debug_traceCall</c> from a sender that has deployed
    /// code is rejected by Execute (EIP-3607, "sender has deployed code") but allowed by Trace, which skips
    /// sender validation. If the BAL workers stay on Execute the trace fails on the sender-code check.
    /// </summary>
    [Test]
    public async Task debug_traceCall_from_contract_sender_traces_on_bal_path()
    {
        TestRpcBlockchain chain = await BuildAmsterdamBalChain();
        // Trace against a non-genesis head: the BAL manager is disabled on genesis, which would fall through
        // to the inner (scoped-adapter) path and hide the bug.
        await chain.AddBlock();

        LegacyTransactionForRpc call = new()
        {
            From = TestItem.AddressA,
            To = TestItem.AddressB,
            Value = 0,
            Gas = 100_000,
            GasPrice = UInt256.Zero,
        };
        // A non-default tracer forces the buffered (eager) path so the trace actually executes; the default
        // struct-log tracer streams lazily and would never run the transaction. The sender has deployed code:
        // Execute rejects it (EIP-3607), Trace skips that check, so a BAL worker stuck on Execute fails here.
        GethTraceOptions options = new()
        {
            Tracer = "callTracer",
            StateOverrides = new Dictionary<Address, AccountOverride>
            {
                { TestItem.AddressA, new AccountOverride { Code = Bytes.FromHexString("0x00") } }
            }
        };

        ResultWrapper<GethLikeTxTrace> result = chain.DebugRpcModule.debug_traceCall(call, BlockParameter.Latest, options);

        Assert.That(result.Result.ResultType, Is.EqualTo(ResultType.Success), () => result.Result.Error ?? string.Empty);
        Assert.That(result.Data, Is.Not.Null);
        Assert.That(result.Data!.Failed, Is.False, "the contract-sender call must trace under skipped validation");
    }
}
