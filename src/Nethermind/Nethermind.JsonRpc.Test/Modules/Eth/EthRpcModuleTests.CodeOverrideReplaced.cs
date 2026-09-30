// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Evm.Precompiles;
using Nethermind.Facade.Eth.RpcTransaction;
using Nethermind.Facade.Proxy.Models.Simulate;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Nethermind.JsonRpc.Test.Modules.Eth;

// A code override is written to the world state and also answered ahead of it by the overridable code repository.
// Code the call itself writes to that address afterwards must replace the override, as it replaces the state.
public partial class EthRpcModuleTests
{
    private static readonly Address OverrideFactory = new("0xc2000000000000000000000000000000000000b1");
    private static readonly Address OverrideDelegate1 = new("0xc2000000000000000000000000000000000000b2");
    private static readonly Address OverrideDelegate2 = new("0xc2000000000000000000000000000000000000b3");
    private static readonly Address OverrideDeployer = new("0xc2000000000000000000000000000000000000b4");
    private static readonly Address OverrideSelfDestructor = new("0xc2000000000000000000000000000000000000b5");
    private static readonly Address OverrideObserver = new("0xc2000000000000000000000000000000000000b6");
    private static readonly Address OverrideReverter = new("0xc2000000000000000000000000000000000000b7");
    private static readonly Address OverrideMovedPrecompile = new("0xc2000000000000000000000000000000000000b8");

    private static byte[] Returning(int value) => Prepare.EvmCode.PushData(value).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;

    /// <summary>Code that self-destructs when called with calldata; called without, it jumps to the JUMPDEST at 7 and returns 42.</summary>
    private static byte[] SelfDestructingOnCalldata() => Prepare.EvmCode
        .Op(Instruction.CALLDATASIZE).Op(Instruction.ISZERO).PushData(7).Op(Instruction.JUMPI)
        .Op(Instruction.CALLER).Op(Instruction.SELFDESTRUCT)
        .Op(Instruction.JUMPDEST).Data(Returning(42)).Done;

    [Test]
    public async Task Code_deployed_over_an_empty_code_override_replaces_it()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Prague.Instance));
        byte[] runtime = Returning(42);
        byte[] init = Prepare.EvmCode.ForInitOf(runtime).Done;
        byte[] salt = new byte[32];
        Address deployed = ContractAddress.From(OverrideFactory, salt, init);

        // The override leaves the target codeless, so CREATE2 may deploy there; then read the size and call it.
        byte[] factory = Prepare.EvmCode
            .Create2(init, salt, 0).Op(Instruction.POP)
            .PushData(deployed).Op(Instruction.EXTCODESIZE).PushData(0x200).Op(Instruction.MSTORE)
            .PushData(32).PushData(0x220).PushData(0).PushData(0).PushData(deployed).PushData(100_000).Op(Instruction.STATICCALL).Op(Instruction.POP)
            .Return(64, 0x200).Done;
        Dictionary<Address, AccountOverride> stateOverride = new()
        {
            [OverrideFactory] = new() { Code = factory },
            [deployed] = new() { Code = [] },
        };
        Transaction tx = Build.A.Transaction.WithTo(OverrideFactory).WithGasLimit(1_000_000).WithGasPrice(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        string serialized = await ctx.Test.TestEthRpc("eth_call", new LegacyTransactionForRpc(tx, new(BlockchainIds.Mainnet)), "latest", stateOverride);

        byte[] expected = Bytes.Concat(((UInt256)runtime.Length).ToBigEndian(), ((UInt256)42).ToBigEndian());
        Assert.That(JToken.Parse(serialized)["result"]?.Value<string>(), Is.EqualTo(expected.ToHexString(true)), serialized);
    }

    [Test]
    public async Task Code_deployed_over_an_empty_code_override_and_reverted_leaves_it_codeless()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Prague.Instance));
        byte[] init = Prepare.EvmCode.ForInitOf(Returning(42)).Done;
        byte[] salt = new byte[32];
        Address deployed = ContractAddress.From(OverrideDeployer, salt, init);

        // The deployer passes the address it created out in its revert data, which shows the write happened.
        byte[] deployer = Prepare.EvmCode
            .Create2(init, salt, 0).PushData(0).Op(Instruction.MSTORE)
            .Revert(32, 0).Done;
        // The write changes the code hash and so ends the override; the revert restores the hash, and with it the empty code.
        byte[] factory = Prepare.EvmCode
            .PushData(32).PushData(0x200).PushData(0).PushData(0).PushData(0).PushData(OverrideDeployer).PushData(200_000).Op(Instruction.CALL).Op(Instruction.POP)
            .PushData(deployed).Op(Instruction.EXTCODESIZE).PushData(0x220).Op(Instruction.MSTORE)
            .Return(64, 0x200).Done;
        Dictionary<Address, AccountOverride> stateOverride = new()
        {
            [OverrideFactory] = new() { Code = factory },
            [OverrideDeployer] = new() { Code = deployer },
            [deployed] = new() { Code = [] },
        };
        Transaction tx = Build.A.Transaction.WithTo(OverrideFactory).WithGasLimit(1_000_000).WithGasPrice(0).SignedAndResolved(TestItem.PrivateKeyA).TestObject;

        string serialized = await ctx.Test.TestEthRpc("eth_call", new LegacyTransactionForRpc(tx, new(BlockchainIds.Mainnet)), "latest", stateOverride);

        byte[] expected = Bytes.Concat(deployed.Bytes.PadLeft(32), new byte[32]);
        Assert.That(JToken.Parse(serialized)["result"]?.Value<string>(), Is.EqualTo(expected.ToHexString(true)), serialized);
    }

    [Test]
    public async Task Delegation_set_over_a_delegation_override_replaces_it([Values] bool clear)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Prague.Instance));
        TestRpcBlockchain test = ctx.Test;

        // The authority is overridden as delegated to the first delegate; the transaction re-delegates it to the second, or clears it.
        Address codeSource = clear ? Address.Zero : OverrideDelegate2;
        AuthorizationTuple authorization = test.EthereumEcdsa.Sign(TestItem.PrivateKeyB, 0, codeSource, test.ReadOnlyState.GetNonce(TestItem.AddressB));
        Transaction tx = Build.A.Transaction
            .WithType(TxType.SetCode)
            .WithTo(TestItem.AddressB)
            .WithGasLimit(500_000)
            .WithMaxFeePerGas(0)
            .WithMaxPriorityFeePerGas(0)
            .WithAuthorizationCode(authorization)
            .SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        Dictionary<Address, AccountOverride> stateOverride = new()
        {
            [TestItem.AddressB] = new() { Code = [.. Eip7702Constants.DelegationHeader, .. OverrideDelegate1.Bytes] },
            [OverrideDelegate1] = new() { Code = Returning(1) },
            [OverrideDelegate2] = new() { Code = Returning(2) },
        };

        string serialized = await test.TestEthRpc("eth_call", new SetCodeTransactionForRpc(tx, new(BlockchainIds.Mainnet)) { GasPrice = null }, "latest", stateOverride);

        // A cleared authority has no code left, so the call to it returns nothing.
        string expected = clear ? "0x" : ((UInt256)2).ToBigEndian().ToHexString(true);
        Assert.That(JToken.Parse(serialized)["result"]?.Value<string>(), Is.EqualTo(expected), serialized);
    }

    // Before Cancun a SELFDESTRUCT deletes the account when its transaction ends, so the later calls of one
    // eth_simulateV1 request, in the same block or the next, must find the overridden account codeless.
    [TestCase(false, false, false)]
    [TestCase(false, false, true)]
    [TestCase(false, true, false)] // A reverted SELFDESTRUCT deletes nothing.
    [TestCase(true, false, false)] // EIP-6780: nor does one of an account the transaction did not create.
    public async Task Selfdestruct_of_a_code_override_is_seen_by_later_calls(bool eip6780, bool reverted, bool nextBlock)
    {
        using Context ctx = await Context.Create(new TestSpecProvider(eip6780 ? Cancun.Instance : Shanghai.Instance));

        byte[] selfDestructor = SelfDestructingOnCalldata();
        // Returns the size of the self-destructor's code and what a call to it returns.
        byte[] observer = Prepare.EvmCode
            .PushData(OverrideSelfDestructor).Op(Instruction.EXTCODESIZE).PushData(0).Op(Instruction.MSTORE)
            .PushData(32).PushData(32).PushData(0).PushData(0).PushData(OverrideSelfDestructor).PushData(100_000).Op(Instruction.STATICCALL).Op(Instruction.POP)
            .Return(64, 0).Done;
        byte[] reverter = Prepare.EvmCode.CallWithInput(OverrideSelfDestructor, 100_000, [1]).Revert(0, 0).Done;
        Dictionary<Address, AccountOverride> stateOverride = new()
        {
            [OverrideSelfDestructor] = new() { Code = selfDestructor },
            [OverrideObserver] = new() { Code = observer },
            [OverrideReverter] = new() { Code = reverter },
        };
        LegacyTransactionForRpc destroy = new() { From = TestItem.AddressA, To = reverted ? OverrideReverter : OverrideSelfDestructor, Input = [1], Gas = 200_000, GasPrice = 0 };
        LegacyTransactionForRpc observe = new() { From = TestItem.AddressA, To = OverrideObserver, Gas = 200_000, GasPrice = 0 };
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls = nextBlock
                ? [new() { StateOverrides = stateOverride, Calls = [destroy] }, new() { Calls = [observe] }]
                : [new() { StateOverrides = stateOverride, Calls = [destroy, observe] }]
        };

        string serialized = await ctx.Test.TestEthRpc("eth_simulateV1", payload);

        byte[] expected = !eip6780 && !reverted
            ? new byte[64]
            : Bytes.Concat(((UInt256)selfDestructor.Length).ToBigEndian(), ((UInt256)42).ToBigEndian());
        Assert.That(JToken.Parse(serialized)["result"]?.Last?["calls"]?.Last?["returnData"]?.Value<string>(), Is.EqualTo(expected.ToHexString(true)), serialized);
    }

    // The address a precompile was moved away from is an ordinary account, so once the code overriding it
    // self-destructs before Cancun it has no code left, and the precompile runs only where it was moved to.
    [Test]
    public async Task Selfdestruct_of_a_code_override_over_a_moved_precompile_leaves_no_code()
    {
        using Context ctx = await Context.Create(new TestSpecProvider(Shanghai.Instance));
        Address identity = IdentityPrecompile.Address;
        Dictionary<Address, AccountOverride> stateOverride = new()
        {
            [identity] = new() { Code = SelfDestructingOnCalldata(), MovePrecompileToAddress = OverrideMovedPrecompile },
        };
        byte[] input = [0xaa, 0xbb];
        LegacyTransactionForRpc destroy = new() { From = TestItem.AddressA, To = identity, Input = [1], Gas = 200_000, GasPrice = 0 };
        LegacyTransactionForRpc callOrigin = new() { From = TestItem.AddressA, To = identity, Input = input, Gas = 200_000, GasPrice = 0 };
        LegacyTransactionForRpc callMoved = new() { From = TestItem.AddressA, To = OverrideMovedPrecompile, Input = input, Gas = 200_000, GasPrice = 0 };
        SimulatePayload<TransactionForRpc> payload = new()
        {
            BlockStateCalls = [new() { StateOverrides = stateOverride, Calls = [destroy, callOrigin, callMoved] }]
        };

        string serialized = await ctx.Test.TestEthRpc("eth_simulateV1", payload);

        JToken? calls = JToken.Parse(serialized)["result"]?[0]?["calls"];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(calls?.Select(static call => call["status"]?.Value<string>()), Is.EqualTo(new[] { "0x1", "0x1", "0x1" }), serialized);
            // The identity precompile echoes its input, so only the call to where it was moved gets the input back.
            Assert.That(calls?.Select(static call => call["returnData"]?.Value<string>()), Is.EqualTo(new[] { "0x", "0x", input.ToHexString(true) }), serialized);
        }
    }
}
