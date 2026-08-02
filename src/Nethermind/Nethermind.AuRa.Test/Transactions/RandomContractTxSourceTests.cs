// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Linq;
using Nethermind.Consensus;
using Nethermind.Consensus.AuRa.Contracts;
using Nethermind.Consensus.AuRa.Transactions;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Test.Builders;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Nethermind.AuRa.Test.Transactions;

[Parallelizable(ParallelScope.All)]
public class RandomContractTxSourceTests
{
    [Test]
    public void Reveal_falls_back_to_previous_key_when_signer_decrypt_returns_failure()
    {
        byte[] secret = Enumerable.Range(1, 32).Select(static i => (byte)i).ToArray();
        byte[] cipher = [1, 2, 3];
        Transaction revealTransaction = Build.A.Transaction.TestObject;

        IRandomContract contract = Substitute.For<IRandomContract>();
        contract.Activation.Returns(0UL);
        contract.GetPhase(Arg.Any<BlockHeader>()).Returns((IRandomContract.Phase.Reveal, UInt256.One));
        contract.GetCommitAndCipher(Arg.Any<BlockHeader>(), in Arg.Any<UInt256>()).Returns((Keccak.Compute(secret), cipher));
        contract.RevealNumber(in Arg.Any<UInt256>()).Returns(revealTransaction);

        ISigner signer = Substitute.For<ISigner>();
        signer.Key.Returns(new PrivateKey("010102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f"));

        IProtectedPrivateKey previousCryptoKey = Substitute.For<IProtectedPrivateKey>();
        previousCryptoKey.Unprotect().Returns(new PrivateKey("020102030405060708090a0b0c0d0e0f000102030405060708090a0b0c0d0e0f"));

        IEciesCipher eciesCipher = Substitute.For<IEciesCipher>();
        (bool Success, byte[]? PlainText) failed = (false, null);
        (bool Success, byte[]? PlainText) succeeded = (true, secret);
        eciesCipher.Decrypt(Arg.Any<PrivateKey>(), cipher).Returns(failed, succeeded);

        RandomContractTxSource source = new(
            [contract],
            eciesCipher,
            signer,
            previousCryptoKey,
            Substitute.For<ICryptoRandom>(),
            LimboLogs.Instance);

        Transaction[] transactions = source.GetTransactions(Build.A.BlockHeader.TestObject, 0, null, false).ToArray();

        Assert.That(transactions, Is.EqualTo(new[] { revealTransaction }));
        previousCryptoKey.Received(1).Unprotect();
    }
}
