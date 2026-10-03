// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Nethermind.Blockchain;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Core.Test.Container;
using Nethermind.Evm;
using Nethermind.Evm.State;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

[NonParallelizable]
public class NativeFrameAssertionsTests
{
    private static readonly Address Body = Address.FromNumber(0xb0d0);
    private static readonly Address Assertion = Address.FromNumber(0xc0de);

    [TestCase("pass")]
    [TestCase("mismatch")]
    [TestCase("static-write")]
    public async Task Lean_dependency_blocks_preserve_state_diff_assertion_semantics(string scenario)
    {
        LeanProofStore proofs = new();
        IReleaseSpec spec = Eip8288Prototype.Instance;
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(spec))
            .AddSingleton(proofs)
            .AddScoped<IGenesisPostProcessor, IWorldState, ISpecProvider>((state, provider) => new FunctionalGenesisPostProcessor(_ =>
            {
                state.CreateAccount(Body, UInt256.Zero);
                state.InsertCode(Body, Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done, provider.GenesisSpec);
                state.CreateAccount(Assertion, UInt256.Zero);
                byte[] assertion = scenario == "static-write"
                    ? Prepare.EvmCode.PushData(1).PushData(0).Op(Instruction.SSTORE).Op(Instruction.STOP).Done
                    : AssertStorageTransition(scenario == "pass" ? UInt256.One : (UInt256)2);
                state.InsertCode(Assertion, assertion, provider.GenesisSpec);
                state.RecalculateStateRoot();
            })));
        FrameDependency signature = NativeLeanProofVerifierTests.Dependency("sphincs");
        FrameDependency stark = NativeLeanProofVerifierTests.Dependency("stark");
        byte[][] witnesses = [NativeLeanProofVerifierTests.Witness("sphincs"), NativeLeanProofVerifierTests.Witness("stark")];
        Assert.That(NativeLeanProofVerifier.Instance.VerifyLeanSphincs(signature.DataHash, signature.VerificationKey, witnesses[0]), Is.True);
        Assert.That(NativeLeanProofVerifier.Instance.VerifyLeanStark(stark.DataHash, stark.VerificationKey, witnesses[1]), Is.True);
        proofs.AddVerified([signature, stark], witnesses, null);
        Transaction transaction = NativeBlockProductionTests.CreateTransaction(chain);
        transaction.Frames = [.. transaction.Frames!,
            new(FrameMode.Sender, FrameFlags.None, Body, 100_000, GasCostOf.SSetState, UInt256.Zero, default),
            new(FrameMode.PostTx, FrameFlags.None, Assertion, 100_000, UInt256.Zero, default)];
        transaction.GasLimit = FrameTxValidation.TotalGasLimit(transaction.Frames);
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        transaction.Hash = transaction.CalculateHash();
        UInt256 balanceBefore;
        using (chain.MainWorldState.BeginScope(chain.BlockTree.Head!.Header))
            balanceBefore = chain.MainWorldState.GetBalance(TestItem.PrivateKeyB.Address);
        Assert.That(chain.TxPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast), Is.EqualTo(AcceptTxResult.Accepted));

        Block block = await chain.AddBlock(TestBlockchainUtil.AddBlockFlags.MayHaveExtraTx);
        TxReceipt receipt = chain.ReceiptStorage.Get(block)[0];
        bool passes = scenario == "pass";
        using (chain.MainWorldState.BeginScope(block.Header))
        using (Assert.EnterMultipleScope())
        {
            Assert.That(block.Transactions, Has.Length.EqualTo(1), "a failing POST_TX assertion remains an included, paid transaction");
            Assert.That(receipt.StatusCode, Is.EqualTo(passes ? StatusCode.Success : StatusCode.Failure));
            Assert.That(receipt.FrameReceipts![^1].Status, Is.EqualTo(passes ? StatusCode.Success : StatusCode.Failure));
            chain.MainWorldState.Get(new StorageCell(Body, UInt256.Zero), out UInt256 stored);
            Assert.That(stored, Is.EqualTo(passes ? UInt256.One : UInt256.Zero));
            chain.MainWorldState.Get(new StorageCell(Assertion, UInt256.Zero), out UInt256 assertionStored);
            Assert.That(assertionStored, Is.EqualTo(UInt256.Zero));
            Assert.That(chain.MainWorldState.GetNonce(TestItem.PrivateKeyB.Address), Is.EqualTo(1));
            Assert.That(chain.MainWorldState.GetBalance(TestItem.PrivateKeyB.Address), Is.LessThan(balanceBefore));
            Assert.That(receipt.Payer, Is.EqualTo(TestItem.PrivateKeyB.Address));
            Assert.That(Eip8288Dependencies.DependencyDeclarationCount(block), Is.EqualTo(3));
            (ulong execution, ulong state) = block.Header.GasUsedPerDimension!.Value;
            Assert.That(state, Is.EqualTo(passes ? (ulong)GasCostOf.SSetState : 0));
            Assert.That(receipt.GasUsed, Is.EqualTo(execution + state));
            Assert.That(Math.Max(execution, state) + 3 * Eip8288Constants.LeanStarkVerificationGas, Is.EqualTo(block.GasUsed));
            ValueHash256 commitment = Eip8288Dependencies.ComputeBlockDepsHash(block);
            Assert.That(NativeLeanProofVerifier.Instance.VerifyRecursiveStark(commitment, Eip8288Constants.AggregatedVk,
                block.Header.RecursiveStark!.StarkProof), Is.True);
        }
    }

    private static byte[] AssertStorageTransition(UInt256 expectedAfter)
    {
        (byte[] code, UInt256 expected)[] checks =
        [
            (Prepare.EvmCode.PushData(0).PushData(Body).PushData(0).Op(Instruction.TXDIFF).Done, UInt256.Zero),
            (Prepare.EvmCode.PushData(0).PushData(Body).PushData(1).Op(Instruction.TXDIFF).Done, expectedAfter)
        ];
        int fail = 1;
        foreach ((byte[] code, _) in checks) fail += code.Length + 39;
        List<byte> result = [];
        foreach ((byte[] code, UInt256 expected) in checks)
        {
            result.AddRange(code);
            result.Add((byte)Instruction.PUSH32);
            byte[] word = new byte[32];
            expected.ToBigEndian(word);
            result.AddRange(word);
            result.Add((byte)Instruction.EQ);
            result.Add((byte)Instruction.ISZERO);
            result.Add((byte)Instruction.PUSH2);
            result.Add((byte)(fail >> 8));
            result.Add((byte)fail);
            result.Add((byte)Instruction.JUMPI);
        }
        result.Add((byte)Instruction.STOP);
        result.Add((byte)Instruction.JUMPDEST);
        result.Add((byte)Instruction.PUSH0);
        result.Add((byte)Instruction.PUSH0);
        result.Add((byte)Instruction.REVERT);
        return [.. result];
    }
}
