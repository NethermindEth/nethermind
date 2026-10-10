// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

// A call with a state override runs in the single-call env, which remembers the code each address resolved to for
// the rest of the call. These probes change code in the middle of the call and read it back through every opcode
// that resolves code, so an entry that outlives the change comes back as a wrong word.
public partial class EthRpcModuleTests
{
    private static readonly Address CodeProbe = new("0xc2000000000000000000000000000000000000a1");
    private static readonly Address DeployAndRevert = new("0xc2000000000000000000000000000000000000a2");
    private static readonly Address DelegateProbe = new("0xc2000000000000000000000000000000000000a3");
    private static readonly byte[] ProbeSalt = new byte[32];

    // Returns 42.
    private static readonly byte[] ProbeRuntime = Prepare.EvmCode.PushData(42).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;
    private static readonly byte[] ProbeInit = Prepare.EvmCode.ForInitOf(ProbeRuntime).Done;
    private static readonly byte[] RevertingInit = Prepare.EvmCode.Revert(0, 0).Done;

    private const int ProbeOutput = 0x200;

    private static int ProbeWord(int index) => ProbeOutput + 32 * index;

    private static IReleaseSpec ResolvedCodeSpec(string fork) => fork switch
    {
        "Prague" => Prague.Instance,
        "Amsterdam" => Amsterdam.Instance,
        // The env records transaction diffs through TracedAccessWorldState once the final spec enables EIP-7906.
        "Amsterdam+EIP-7906" => new OverridableReleaseSpec(Amsterdam.Instance) { IsEip7906Enabled = true },
        _ => throw new ArgumentOutOfRangeException(nameof(fork), fork, null)
    };

    [Test]
    public async Task Eth_call_reads_back_code_deployed_earlier_in_the_call(
        [Values("Prague", "Amsterdam", "Amsterdam+EIP-7906")] string fork)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(ResolvedCodeSpec(fork)));

        string serialized = await ctx.Test.TestEthRpc("eth_call", ProbeCall(CodeProbe, gasPrice: 0), "latest", DeploymentProbeOverrides(revertOnMismatch: false));

        Assert.That(JToken.Parse(serialized)["result"]?.Value<string>(), Is.EqualTo(Bytes.Concat(ExpectedDeploymentProbeWords()).ToHexString(true)), serialized);
    }

    [Test]
    public async Task Rerun_methods_read_back_code_deployed_earlier_in_each_run(
        [Values("eth_estimateGas", "eth_createAccessList")] string method,
        [Values("Prague", "Amsterdam", "Amsterdam+EIP-7906")] string fork)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(ResolvedCodeSpec(fork)));

        // Both methods run the call again from the same state; the probe reverts when a word is wrong.
        string serialized = await ctx.Test.TestEthRpc(method, ProbeCall(CodeProbe, gasPrice: null), "latest", DeploymentProbeOverrides(revertOnMismatch: true));

        AssertExecutedWithoutError(serialized);
    }

    [Test]
    public async Task Code_resolved_by_one_call_is_not_served_to_the_next()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Prague.Instance));

        // The second call rents the pooled env of the first and overrides the same address with other code.
        for (int value = 1; value <= 2; value++)
        {
            Dictionary<Address, AccountOverride> stateOverride = new()
            {
                [CodeProbe] = new() { Code = Prepare.EvmCode.PushData(value).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done }
            };

            string serialized = await ctx.Test.TestEthRpc("eth_call", ProbeCall(CodeProbe, gasPrice: 0), "latest", stateOverride);

            Assert.That(JToken.Parse(serialized)["result"]?.Value<string>(), Is.EqualTo(((UInt256)value).ToBigEndian().ToHexString(true)), serialized);
        }
    }

    [Test]
    public async Task Authority_code_is_read_as_the_designator_after_its_delegated_code_ran(
        [Values("eth_call", "eth_estimateGas")] string method,
        [Values("Prague", "Amsterdam")] string fork,
        [Values] bool delegatedByTheTransaction)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(ResolvedCodeSpec(fork)));
        TestRpcBlockchain test = ctx.Test;
        bool revertOnMismatch = method != "eth_call";

        // The transaction runs the authority, which before EIP-8037 resolves the delegate's code for it; the delegate
        // then reads the authority's own code, which must stay the designator.
        byte[] designator = [.. Eip7702Constants.DelegationHeader, .. DelegateProbe.Bytes];
        byte[][] expected = [((UInt256)designator.Length).ToBigEndian(), designator.PadRight(32), Keccak.Compute(designator).BytesToArray()];
        Prepare delegateCode = Prepare.EvmCode
            .Op(Instruction.ADDRESS).Op(Instruction.EXTCODESIZE).PushData(ProbeWord(0)).Op(Instruction.MSTORE)
            .PushData(32).PushData(0).PushData(ProbeWord(1)).Op(Instruction.ADDRESS).Op(Instruction.EXTCODECOPY)
            .Op(Instruction.ADDRESS).Op(Instruction.EXTCODEHASH).PushData(ProbeWord(2)).Op(Instruction.MSTORE);
        if (revertOnMismatch) RevertUnlessWordsMatch(delegateCode, expected);
        delegateCode.Return(expected.Length * 32, ProbeOutput);
        Dictionary<Address, AccountOverride> stateOverride = new() { [DelegateProbe] = new() { Code = delegateCode.Done } };

        TransactionForRpc transaction;
        if (delegatedByTheTransaction)
        {
            // The authorization writes the authority's code, which keeps it out of the memo for the rest of the call.
            AuthorizationTuple authorization = test.EthereumEcdsa.Sign(TestItem.PrivateKeyB, 0, DelegateProbe, test.ReadOnlyState.GetNonce(TestItem.AddressB));
            Transaction tx = Build.A.Transaction
                .WithType(TxType.SetCode)
                .WithTo(TestItem.AddressB)
                .WithGasLimit(500_000)
                .WithMaxFeePerGas(0)
                .WithMaxPriorityFeePerGas(0)
                .WithAuthorizationCode(authorization)
                .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
            // Priced by its fee caps only: a request that also carries gasPrice is rejected.
            transaction = new SetCodeTransactionForRpc(tx, new(BlockchainIds.Mainnet)) { GasPrice = null };
        }
        else
        {
            // Delegated before the call: nothing writes the authority's code, so only the delegation guard keeps
            // the delegate's code from being remembered for it.
            stateOverride[TestItem.AddressB] = new() { Code = designator };
            transaction = ProbeCall(TestItem.AddressB, gasPrice: 0);
        }

        string serialized = await test.TestEthRpc(method, transaction, "latest", stateOverride);

        if (revertOnMismatch)
            AssertExecutedWithoutError(serialized);
        else
            Assert.That(JToken.Parse(serialized)["result"]?.Value<string>(), Is.EqualTo(Bytes.Concat(expected).ToHexString(true)), serialized);
    }

    // eth_createAccessList reports a reverted run in its result, not as a JSON-RPC error.
    private static void AssertExecutedWithoutError(string serialized)
    {
        JToken response = JToken.Parse(serialized);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(response["error"], Is.Null, serialized);
            Assert.That(response["result"], Is.Not.Null, serialized);
            Assert.That((response["result"] as JObject)?["error"], Is.Null, serialized);
        }
    }

    private static LegacyTransactionForRpc ProbeCall(Address to, ulong? gasPrice)
    {
        Transaction tx = Build.A.Transaction.WithTo(to).WithGasLimit(1_000_000).WithGasPrice(gasPrice ?? 0)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        LegacyTransactionForRpc transaction = new(tx, new(BlockchainIds.Mainnet));
        // An access list request with a zero gas price after London is rejected before it runs.
        if (gasPrice is null) transaction.GasPrice = null;
        return transaction;
    }

    /// <summary>
    /// The probe at <see cref="CodeProbe"/> and its helper at <see cref="DeployAndRevert"/>; see
    /// <see cref="ExpectedDeploymentProbeWords"/> for what each word of the output holds.
    /// </summary>
    private static Dictionary<Address, AccountOverride> DeploymentProbeOverrides(bool revertOnMismatch)
    {
        Address deployed = ContractAddress.From(CodeProbe, ProbeSalt, ProbeInit);
        Address failed = ContractAddress.From(CodeProbe, ProbeSalt, RevertingInit);
        Address deployedAndReverted = ContractAddress.From(DeployAndRevert, ProbeSalt, ProbeInit);

        Prepare probe = Prepare.EvmCode;
        // Resolved before anything is deployed at them, so each is remembered as empty.
        StoreCodeSize(probe, deployed, 0);
        StoreCodeSize(probe, failed, 1);
        StoreCodeSize(probe, deployedAndReverted, 2);
        probe.Create2(RevertingInit, ProbeSalt, 0).Op(Instruction.POP)
            .Create2(ProbeInit, ProbeSalt, 0).Op(Instruction.POP);
        StoreCodeSize(probe, deployed, 3);
        probe.PushData(deployed).Op(Instruction.EXTCODEHASH).PushData(ProbeWord(4)).Op(Instruction.MSTORE)
            // EXTCODECOPY(address, destOffset, offset, size)
            .PushData(32).PushData(0).PushData(ProbeWord(5)).PushData(deployed).Op(Instruction.EXTCODECOPY)
            // STATICCALL(gas, address, argsOffset, argsSize, retOffset, retSize)
            .PushData(32).PushData(ProbeWord(6)).PushData(0).PushData(0).PushData(deployed).PushData(100_000).Op(Instruction.STATICCALL).Op(Instruction.POP);
        StoreCodeSize(probe, failed, 7);
        probe.Call(DeployAndRevert, 300_000).Op(Instruction.POP);
        StoreCodeSize(probe, deployedAndReverted, 8);
        byte[][] expected = ExpectedDeploymentProbeWords();
        if (revertOnMismatch) RevertUnlessWordsMatch(probe, expected);
        probe.Return(expected.Length * 32, ProbeOutput);

        // Deploys, reads the new code through EXTCODESIZE and STATICCALL, then reverts the deployment.
        byte[] deployAndRevert = Prepare.EvmCode
            .Create2(ProbeInit, ProbeSalt, 0).Op(Instruction.POP)
            .PushData(deployedAndReverted).Op(Instruction.EXTCODESIZE).Op(Instruction.POP)
            .PushData(0).PushData(0).PushData(0).PushData(0).PushData(deployedAndReverted).PushData(50_000).Op(Instruction.STATICCALL).Op(Instruction.POP)
            .Revert(0, 0).Done;

        return new()
        {
            [CodeProbe] = new() { Code = probe.Done },
            [DeployAndRevert] = new() { Code = deployAndRevert },
        };
    }

    private static byte[][] ExpectedDeploymentProbeWords() =>
    [
        new byte[32],                                          // 0: deployment target, before the deployment
        new byte[32],                                          // 1: target of a deployment whose constructor reverts, before it
        new byte[32],                                          // 2: target of a deployment in a reverted frame, before it
        ((UInt256)ProbeRuntime.Length).ToBigEndian(),          // 3: EXTCODESIZE of the new contract
        Keccak.Compute(ProbeRuntime).BytesToArray(),           // 4: EXTCODEHASH of the new contract
        ProbeRuntime.PadRight(32),                             // 5: EXTCODECOPY of the new contract
        ((UInt256)42).ToBigEndian(),                           // 6: what STATICCALL to the new contract returns
        new byte[32],                                          // 7: the failed deployment's target, after it
        new byte[32],                                          // 8: the reverted deployment's target, after the revert
    ];

    private static void StoreCodeSize(Prepare code, Address address, int word) =>
        code.PushData(address).Op(Instruction.EXTCODESIZE).PushData(ProbeWord(word)).Op(Instruction.MSTORE);

    private static void RevertUnlessWordsMatch(Prepare code, byte[][] expected)
    {
        byte[] revert = Prepare.EvmCode.Data([(byte)Instruction.PUSH1, 0, (byte)Instruction.PUSH1, 0, (byte)Instruction.REVERT]).Done;
        for (int i = 0; i < expected.Length; i++)
        {
            code.PushData((ReadOnlyMemory<byte>)expected[i]).PushData(ProbeWord(i)).Op(Instruction.MLOAD).Op(Instruction.EQ);
            // PUSH2 target, JUMPI, the revert, then the JUMPDEST the check lands on.
            int target = code.Done.Length + 3 + 1 + revert.Length;
            code.PushData((ReadOnlyMemory<byte>)new[] { (byte)(target >> 8), (byte)target }).Op(Instruction.JUMPI)
                .Data(revert).Op(Instruction.JUMPDEST);
        }
    }
}
