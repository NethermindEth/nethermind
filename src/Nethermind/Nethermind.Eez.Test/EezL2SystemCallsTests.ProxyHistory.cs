// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Text.Json;
using Autofac;
using Nethermind.Blockchain.Tracing;
using Nethermind.Consensus.Processing;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Exceptions;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Eez.Execution;
using Nethermind.Eez.Execution.Settlement.RollupX;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs.Forks;
using NUnit.Framework;

namespace Nethermind.Eez.Test;

public partial class EezL2SystemCallsTests
{
    [Test]
    public void Process_ProxyHistory_EmptyStoreOrRecordedImportUsesExactOrigin([Values("omitted", "bootstrap", "advanced", "sparse")] string selection) =>
        WithProxyHistory((scope, state, header, chain, rules) =>
        {
            NativeHistoryBlock[]? records = selection switch
            {
                "bootstrap" => [new(99, chain.At(99).Hash)],
                "advanced" => [new(100, chain.At(100).Hash)],
                "sparse" => [new(99, chain.At(99).Hash), new(101, chain.At(101).Hash)],
                _ => null
            };
            ulong origin = records is null ? 99 : records[^1].Number;
            Block block = ProxyHistoryBlock(header, chain, records);
            TxReceipt[] receipts = ExecuteHistory(ProxyHistoryExecutor(scope, state, chain, rules), block);
            state.Get(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)1), out UInt256 storedNumber);
            state.Get(new StorageCell(EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(origin)), out UInt256 storedHash);
            using (Assert.EnterMultipleScope())
            {
                Assert.That(storedNumber, Is.EqualTo(records is null ? UInt256.Zero : (UInt256)origin));
                Assert.That(storedHash, Is.EqualTo(records is null ? UInt256.Zero : chain.At(origin).Hash.ToUInt256()));
                Assert.That(ReadStorage(state, TestItem.AddressB), Is.EqualTo(chain.At(origin).PrevRandao.ToUInt256()));
                Assert.That(receipts.Length, Is.EqualTo(records is null ? 1 : 2));
                Assert.That(receipts[^1].StatusCode, Is.EqualTo(StatusCode.Success));
                Assert.That(state.GetNonce(Address.SystemUser), Is.Zero);
                if (selection == "sparse")
                {
                    state.Get(new StorageCell(EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(100)), out UInt256 omittedHash);
                    Assert.That(omittedHash, Is.EqualTo(UInt256.Zero), "the ancestry bridge is authenticated but is not automatically published");
                }
            }
        });

    [Test]
    public void Process_ProxyHistory_IncreasedProxyNonceAndChangedImplementation_AreResolvedFromState([Values(2UL, 3UL)] ulong nonce) =>
        WithProxyHistory((scope, state, header, chain, rules) =>
        {
            Address replacement = TestItem.AddressC;
            JsonElement alloc = StatelessFixtures.ReadJson("rollupx/native-profile-v6", "genesis.json").GetProperty("alloc");
            byte[] runtime = Bytes.FromHexString(alloc.GetProperty(NativeProxyHistory.ImplementationAddress.ToString()).GetProperty("code").GetString()!);
            state.CreateAccount(replacement, 0, 1);
            state.InsertCode(replacement, runtime, Osaka.Instance);
            state.Set(new StorageCell(EezConstants.L1HistoryAddress, NativeProxyHistory.ImplementationSlot), new UInt256(replacement.Bytes, isBigEndian: true));
            state.SetNonce(EezConstants.L1HistoryAddress, nonce);
            state.Commit(Osaka.Instance);
            TxReceipt[] receipts = ExecuteHistory(ProxyHistoryExecutor(scope, state, chain, rules),
                ProxyHistoryBlock(header, chain, [new(100, chain.At(100).Hash)]));
            using (Assert.EnterMultipleScope())
            {
                Assert.That(receipts[0].StatusCode, Is.EqualTo(StatusCode.Success));
                Assert.That(ReadStorage(state, TestItem.AddressB), Is.EqualTo(chain.At(100).PrevRandao.ToUInt256()));
                Assert.That(state.GetNonce(EezConstants.L1HistoryAddress), Is.EqualTo(nonce));
            }
        });

    [Test]
    public void Process_ProxyHistory_BadImplementationResult_InvalidatesBlock([Values("output", "logs", "revert")] string result) =>
        WithProxyHistory((scope, state, header, chain, rules) =>
        {
            byte[] runtime = result switch
            {
                "output" => [0x60, 1, 0x5f, 0x53, 0x60, 1, 0x5f, 0xf3],
                "logs" => [0x5f, 0x5f, 0xa0, 0x00],
                _ => [0x5f, 0x5f, 0xfd]
            };
            state.InsertCode(NativeProxyHistory.ImplementationAddress, runtime, Osaka.Instance);
            state.Commit(Osaka.Instance);
            Assert.That(() => ExecuteHistory(ProxyHistoryExecutor(scope, state, chain, rules),
                ProxyHistoryBlock(header, chain, [new(100, chain.At(100).Hash)])), Throws.TypeOf<InvalidBlockException>());
            Assert.That(state.GetNonce(TestItem.PrivateKeyA.Address), Is.Zero, "the application must not run after a bad history result");
        });

    [Test]
    public void Process_ProxyHistory_WrongRandomness_OnlyExplicitV6DerivationCanReplaceIt([Values] bool derived, [Values] bool producing) =>
        WithProxyHistory((scope, state, header, chain, rules) =>
        {
            Block block = ProxyHistoryBlock(header, chain, [new(100, chain.At(100).Hash)]);
            header.MixHash = TestItem.KeccakD;
            NativeHistoryBlockExecutor executor = new(scope.Resolve<IBlockProcessor.IBlockTransactionsExecutor>(), state,
                scope.Resolve<ISpecProvider>(), rules, HistorySchedule, 99, null, chain)
            { DeriveOrigin = derived };
            using BlockReceiptsTracer tracer = new();
            tracer.StartNewBlockTrace(block);
            if (derived && producing)
            {
                executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, tracer);
                Assert.That(header.MixHash, Is.EqualTo(new Hash256(chain.At(100).PrevRandao)));
            }
            else
                Assert.That(() => executor.ProcessTransactions(block, producing ? ProcessingOptions.ProducingBlock : ProcessingOptions.None, tracer),
                    Throws.TypeOf<InvalidBlockException>());
        });

    [Test]
    public void Process_ContractV3History_DerivationMarkerCannotReplaceWrongRandomness() =>
        WithHistory((scope, state, header, chain) =>
        {
            Block block = ContractV3HistoryBlock(header, chain, true);
            header.MixHash = TestItem.KeccakD;
            NativeHistoryBlockExecutor executor = new(scope.Resolve<IBlockProcessor.IBlockTransactionsExecutor>(), state,
                scope.Resolve<ISpecProvider>(), ContractV3HistoryRules(chain), HistorySchedule, 99, HistorySource, chain)
            { DeriveOrigin = true };
            using BlockReceiptsTracer tracer = new();
            tracer.StartNewBlockTrace(block);
            Assert.That(() => executor.ProcessTransactions(block, ProcessingOptions.ProducingBlock, tracer), Throws.TypeOf<InvalidBlockException>());
        }, contractV3: true);

    [Test]
    public void Process_ProxyHistory_InvalidParent_RejectsBeforeApplication([Values("code", "nonce", "wide", "zero-hash", "block-zero")] string field) =>
        WithProxyHistory((scope, state, header, chain, rules) =>
        {
            switch (field)
            {
                case "code": state.InsertCode(EezConstants.L1HistoryAddress, StopCode, Osaka.Instance); break;
                case "nonce": state.SetNonce(EezConstants.L1HistoryAddress, 1); break;
                case "wide": state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)1), UInt256.MaxValue); break;
                case "zero-hash": state.Set(new StorageCell(EezConstants.L1HistoryAddress, (UInt256)1), 99); break;
                case "block-zero": state.Set(new StorageCell(EezConstants.L1HistoryAddress, NativeHistoryBlockExecutor.HashSlot(0)), 1); break;
            }
            state.Commit(Osaka.Instance);
            AssertHistoryRejected(ProxyHistoryExecutor(scope, state, chain, rules), ProxyHistoryBlock(header, chain, null), state);
        });

    private static NativeHistoryBlockExecutor ProxyHistoryExecutor(ILifetimeScope scope, IWorldState state, NativeL1HeaderChain chain, EezChainRules rules) =>
        new(scope.Resolve<IBlockProcessor.IBlockTransactionsExecutor>(), state, scope.Resolve<ISpecProvider>(), rules,
            HistorySchedule, 99, null, chain);

    private static Block ProxyHistoryBlock(BlockHeader header, NativeL1HeaderChain chain, NativeHistoryBlock[]? records)
    {
        header.Timestamp = 126;
        header.ParentHash = TestItem.KeccakD;
        header.MixHash = new Hash256(chain.At(records is null ? 99 : records[^1].Number).PrevRandao);
        Transaction app = Build.A.Transaction.WithChainId(1).WithTo(TestItem.AddressB).WithValue(0).WithGasLimit(100_000)
            .WithGasPrice(2.GWei).SignedAndResolved(TestItem.PrivateKeyA).TestObject;
        return new Block(header, records is null ? [app] : [new NativeHistoryTransaction(NativeHistoryEnvelope.CreateBlocks(1, records)), app], []);
    }

    private static void WithProxyHistory(Action<ILifetimeScope, IWorldState, BlockHeader, NativeL1HeaderChain, EezChainRules> action)
    {
        NativeL1HeaderChain chain = ApplicationHistoryChain();
        EezChainRules rules = EezChainRules.NativeRollupX(ContractV3HistoryRules(chain).Schedule!, TestItem.AddressD, NativeSystemTxFormat.ContractV6, 2);
        WithProcessor(rules, (scope, state, _, header) =>
        {
            NativeHistoryFixtures.InstallProxy(state);
            state.CreateAccount(TestItem.PrivateKeyA.Address, 1.Ether);
            state.CreateAccount(TestItem.AddressB, 0, 1);
            state.InsertCode(TestItem.AddressB, new byte[] { 0x44, 0x5f, 0x55, 0x00 }, Osaka.Instance);
            state.Commit(Osaka.Instance);
            action(scope, state, header, chain, rules);
        });
    }
}
