// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Extensions;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm;
using Nethermind.Facade.Eth.RpcTransaction;
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

    private static byte[] Returning(int value) => Prepare.EvmCode.PushData(value).PushData(0).Op(Instruction.MSTORE).Return(32, 0).Done;

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
        // The write drops the override entry without journaling it, so the revert must bring the empty code back through the state.
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
}
