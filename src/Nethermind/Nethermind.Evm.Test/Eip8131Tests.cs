// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using Nethermind.Blockchain;
using Nethermind.Blockchain.Tracing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Evm.State;
using Nethermind.Evm.TransactionProcessing;
using Nethermind.Int256;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.Specs.Test;
using NUnit.Framework;

namespace Nethermind.Evm.Test;

/// <summary>
/// Tests for EIP-8131: unified transaction content floor.
/// </summary>
[TestFixture]
public class Eip8131Tests
{
    // Without EIP-2780 the floor is anchored on TX_BASE = 21000.
    private static readonly IReleaseSpec PragueSpec = new OverridableReleaseSpec(Prague.Instance)
    {
        IsEip7976Enabled = true,
        IsEip7981Enabled = true,
        IsEip8131Enabled = true,
    };

    // With EIP-2780 the floor is anchored on the decomposed intrinsic base, which is 21000 for a value transfer to another account.
    private static readonly IReleaseSpec AmsterdamSpec = new OverridableReleaseSpec(Amsterdam.Instance) { IsEip8131Enabled = true };

    private static IEnumerable<IReleaseSpec> Specs() => [PragueSpec, AmsterdamSpec];

    private static TransactionBuilder<Transaction> ValueTransfer(TxType type) => Build.A.Transaction
        .WithType(type)
        .WithSenderAddress(TestItem.AddressA)
        .WithTo(TestItem.AddressB)
        .WithValue(1.Ether);

    private static AuthorizationTuple Authorization() => new(1, TestItem.AddressC, 0, new Signature(new byte[64], 0));

    private static IEnumerable<TestCaseData> EipTestCases()
    {
        yield return new TestCaseData(ValueTransfer(TxType.Legacy).TestObject, 21_000UL)
            .SetName("Bare ETH transfer");

        foreach (TxType type in (TxType[])[TxType.Legacy, TxType.AccessList, TxType.EIP1559, TxType.Blob, TxType.SetCode])
        {
            yield return new TestCaseData(ValueTransfer(type).WithData(new byte[10_000]).TestObject, 661_000UL)
                .SetName($"Calldata-only, 10000 bytes ({type})");
        }

        yield return new TestCaseData(
                ValueTransfer(TxType.AccessList).WithAccessList(new AccessList.Builder().AddAddress(Address.Zero).AddStorage(UInt256.One).Build()).TestObject,
                24_328UL)
            .SetName("Access list: address with one storage key");

        yield return new TestCaseData(ValueTransfer(TxType.SetCode).WithAuthorizationCode(Authorization()).TestObject, 27_912UL)
            .SetName("EIP-7702 with one authorization");

        yield return new TestCaseData(ValueTransfer(TxType.Blob).WithBlobVersionedHashes(6).TestObject, 33_288UL)
            .SetName("EIP-4844 with six blob versioned hashes");

        // 21000 + 64 * (3 + 2 * 20 + 3 * 32 + 2 * 108)
        yield return new TestCaseData(
                ValueTransfer(TxType.SetCode)
                    .WithData([0, 1, 2])
                    .WithAccessList(new AccessList.Builder()
                        .AddAddress(TestItem.AddressC).AddStorage(UInt256.Zero).AddStorage(UInt256.One)
                        .AddAddress(TestItem.AddressD).AddStorage(UInt256.One)
                        .Build())
                    .WithAuthorizationCode([Authorization(), Authorization()])
                    .TestObject,
                43_720UL)
            .SetName("All content fields combined");
    }

    [TestCaseSource(nameof(EipTestCases))]
    public void Floor_matches_eip_test_cases(Transaction transaction, ulong expectedFloor)
    {
        foreach (IReleaseSpec spec in Specs())
        {
            Assert.That(IntrinsicGasCalculator.Calculate(transaction, spec).FloorGas, Is.EqualTo(expectedFloor), spec.Name);
        }
    }

    [TestCaseSource(nameof(EipTestCases))]
    public void Intrinsic_gas_carries_no_content_surcharge(Transaction transaction, ulong _)
    {
        foreach (IReleaseSpec spec in Specs())
        {
            IReleaseSpec withoutFloorSurcharges = new OverridableReleaseSpec(spec) { IsEip7981Enabled = false, IsEip8131Enabled = false };
            Assert.That(
                IntrinsicGasCalculator.Calculate(transaction, spec).Standard,
                Is.EqualTo(IntrinsicGasCalculator.Calculate(transaction, withoutFloorSurcharges).Standard),
                spec.Name);
        }
    }

    // Six blob hashes: intrinsic 21000, content floor 33288.
    [TestCase(33_287UL, false, TestName = "Gas limit one below content floor is rejected")]
    [TestCase(33_288UL, true, TestName = "Gas limit at content floor is executed")]
    [TestCase(100_000UL, true, TestName = "Gas limit above content floor spends the floor")]
    public void Content_floor_bounds_gas_limit_and_gas_used(ulong gasLimit, bool executed)
    {
        const ulong floor = 33_288;
        TestSpecProvider specProvider = new(PragueSpec);
        IWorldState state = TestWorldStateFactory.CreateForTest();
        using IDisposable scope = state.BeginScope(IWorldState.PreGenesis);
        state.CreateAccount(TestItem.AddressA, 1.Ether);
        state.Commit(specProvider.GenesisSpec);
        state.CommitTree(0);

        EthereumVirtualMachine virtualMachine = new(new TestBlockhashProvider(specProvider), specProvider, LimboLogs.Instance);
        EthereumTransactionProcessor processor = new(BlobBaseFeeCalculator.Instance, specProvider, state, virtualMachine, new EthereumCodeInfoRepository(state), LimboLogs.Instance);

        Transaction tx = Build.A.Transaction
            .WithTo(TestItem.AddressB)
            .WithMaxFeePerGas(1)
            .WithMaxPriorityFeePerGas(1)
            .WithGasLimit(gasLimit)
            .WithShardBlobTxTypeAndFields(6, isMempoolTx: false)
            .SignedAndResolved(new EthereumEcdsa(specProvider.ChainId), TestItem.PrivateKeyA)
            .TestObject;
        Block block = Build.A.Block.WithNumber(1).WithGasLimit(1_000_000).WithBaseFeePerGas(1).WithExcessBlobGas(0).WithTransactions(tx).TestObject;

        CallOutputTracer tracer = new();
        TransactionResult result = processor.Execute(tx, new BlockExecutionContext(block.Header, PragueSpec), tracer);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(result.TransactionExecuted, Is.EqualTo(executed));
            Assert.That(result.Error, Is.EqualTo(executed ? TransactionResult.ErrorType.None : TransactionResult.ErrorType.GasLimitBelowFloorGas));
            Assert.That(tracer.GasSpent, Is.EqualTo(executed ? floor : 0UL));
        }
    }
}
