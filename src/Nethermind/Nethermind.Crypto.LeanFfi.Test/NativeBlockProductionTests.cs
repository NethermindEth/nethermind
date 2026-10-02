// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Blockchain;
using Nethermind.Core.Test.Builders;
using Nethermind.Consensus.Validators;
using Nethermind.Int256;
using Nethermind.Specs;
using Nethermind.Specs.Forks;
using Nethermind.TxPool;
using NUnit.Framework;

namespace Nethermind.Crypto.LeanFfi.Test;

[NonParallelizable]
public class NativeBlockProductionTests
{
    [Test]
    public async Task Produces_and_validates_real_mixed_dependency_block()
    {
        FrameDependency signature = NativeLeanProofVerifierTests.Dependency("sphincs");
        FrameDependency stark = NativeLeanProofVerifierTests.Dependency("stark");
        LeanProofStore proofs = new();
        using BasicTestBlockchain chain = await BasicTestBlockchain.Create(builder => builder
            .AddSingleton<ISpecProvider>(new TestSingleReleaseSpecProvider(Eip8288Prototype.Instance))
            .AddSingleton(proofs));
        Transaction transaction = CreateTransaction(chain);
        AcceptTxResult missing = chain.TxPool.SubmitTx(transaction, TxHandlingOptions.None);
        Assert.That(missing.ToString(), Does.Contain("MissingDependencyProof"));

        byte[][] witnesses = [NativeLeanProofVerifierTests.Witness("sphincs"), NativeLeanProofVerifierTests.Witness("stark")];
        Assert.That(NativeLeanProofVerifier.Instance.VerifyLeanSphincs(signature.DataHash, signature.VerificationKey, witnesses[0]), Is.True);
        Assert.That(NativeLeanProofVerifier.Instance.VerifyLeanStark(stark.DataHash, stark.VerificationKey, witnesses[1]), Is.True);
        proofs.AddVerified([signature, stark], witnesses, null);
        transaction.Hash = transaction.CalculateHash();
        ValidationResult wellFormed = new TxValidator(chain.SpecProvider.ChainId).IsWellFormed(transaction, Eip8288Prototype.Instance);
        Assert.That((bool)wellFormed, Is.True, wellFormed.ToString());
        AcceptTxResult accepted = chain.TxPool.SubmitTx(transaction, TxHandlingOptions.PersistentBroadcast);
        Assert.That(accepted, Is.EqualTo(AcceptTxResult.Accepted), accepted.ToString());
        Block block = await chain.AddBlock(TestBlockchainUtil.AddBlockFlags.MayHaveExtraTx);
        ValueHash256 commitment = Eip8288Dependencies.ComputeBlockDepsHash(block);
        Assert.That(block.Transactions, Has.Length.EqualTo(1));
        Assert.That(block.Header.RecursiveStark, Is.Not.Null);
        Assert.That(block.Header.GasUsedPerDimension, Is.Not.Null);
        (ulong Execution, ulong State) dimensions = block.Header.GasUsedPerDimension!.Value;
        using (Assert.EnterMultipleScope())
        {
            Assert.That(Eip8288Dependencies.ForBlock(block), Has.Count.EqualTo(2));
            Assert.That(Eip8288Dependencies.DependencyDeclarationCount(block), Is.EqualTo(3));
            Assert.That(block.GasUsed, Is.LessThanOrEqualTo(block.GasLimit));
            Assert.That(Math.Max(dimensions.Execution, dimensions.State) + 3 * Eip8288Constants.LeanStarkVerificationGas, Is.EqualTo(block.GasUsed));
            Assert.That(NativeLeanProofVerifier.Instance.VerifyRecursiveStark(commitment, Eip8288Constants.AggregatedVk,
                block.Header.RecursiveStark!.StarkProof), Is.True);
            Assert.That(chain.ReceiptStorage.Get(block)[0].GasUsed + 3 * Eip8288Constants.LeanStarkVerificationGas, Is.EqualTo(block.GasUsed));
        }
    }
    internal static Transaction CreateTransaction(BasicTestBlockchain chain)
    {
        FrameDependency signature = NativeLeanProofVerifierTests.Dependency("sphincs");
        FrameDependency stark = NativeLeanProofVerifierTests.Dependency("stark");
        TxFrame[] frames =
        [
            FrameTxTestFrames.SelfVerify(FrameTxTestFrames.PrefixFrameGas),
            new(FrameMode.DepVerify, FrameFlags.None, null,
                2 * Eip8288Constants.LeanSphincsVerificationGas + Eip8288Constants.LeanStarkVerificationGas,
                UInt256.Zero, Eip8288Dependencies.Serialize([signature, stark, signature]))
        ];
        Transaction transaction = new()
        {
            Type = TxType.FrameTx,
            ChainId = chain.SpecProvider.ChainId,
            SenderAddress = TestItem.PrivateKeyB.Address,
            NonceKeys = [UInt256.Zero],
            Frames = frames,
            GasLimit = FrameTxValidation.TotalGasLimit(frames),
            GasPrice = 1.GWei,
            DecodedMaxFeePerGas = 100.GWei,
        };
        FrameTxTestFrames.SignSecp256k1(transaction, TestItem.PrivateKeyB, TestItem.PrivateKeyB.Address);
        transaction.Hash = transaction.CalculateHash();
        return transaction;
    }

}
