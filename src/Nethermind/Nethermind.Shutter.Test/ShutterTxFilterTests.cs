// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using NUnit.Framework;
using Nethermind.Logging;
using Nethermind.Specs;
using Nethermind.Core.Test.Builders;

namespace Nethermind.Shutter.Test;

[TestFixture]
class ShutterTxFilterTests
{
    [Test]
    public void Accepts_valid_txs()
    {
        Transaction tx0 = Build.A.Transaction
            .WithChainId(BlockchainIds.Chiado)
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(TestItem.AddressA)
            .WithValue(100)
            .Signed(TestItem.PrivateKeyA).TestObject;

         Transaction tx1 = Build.A.Transaction
            .WithChainId(BlockchainIds.Chiado)
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(TestItem.AddressA)
            .WithValue(100)
            .WithType(TxType.EIP1559)
            .WithMaxFeePerGas(4)
            .WithGasLimit(21000)
            .Signed(TestItem.PrivateKeyA).TestObject;

        Assert.Multiple(() =>
        {
            Assert.That(IsAllowed(tx0));
            Assert.That(IsAllowed(tx1));
        });
    }

    [Test]
    public void Rejects_blob_tx()
    {
        Transaction tx = Build.A.Transaction
            .WithChainId(BlockchainIds.GenericNonRealNetwork)
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(TestItem.AddressA)
            .WithType(TxType.Blob)
            .WithMaxFeePerBlobGas(2)
            .WithBlobVersionedHashes(0)
            .Signed(TestItem.PrivateKeyA).TestObject;

        Assert.That(IsAllowed(tx), Is.False);
    }

    [Test]
    public void Rejects_wrong_chain_id()
    {
        Transaction tx = Build.A.Transaction
            .WithChainId(BlockchainIds.GenericNonRealNetwork)
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(TestItem.AddressA)
            .WithValue(100)
            .Signed(TestItem.PrivateKeyA).TestObject;

        Assert.That(IsAllowed(tx), Is.False);
    }

    [Test]
    public void Rejects_bad_signature()
    {
        Core.Crypto.Signature sig = new(new byte[65]);

        Transaction tx = Build.A.Transaction
            .WithChainId(BlockchainIds.Chiado)
            .WithSenderAddress(TestItem.AddressA)
            .WithTo(TestItem.AddressA)
            .WithValue(100)
            .WithSignature(sig).TestObject;

        Assert.That(IsAllowed(tx), Is.False);
    }

    private static bool IsAllowed(Transaction tx)
    {
        ShutterTxFilter txFilter = new(ChiadoSpecProvider.Instance, LimboLogs.Instance);
        return txFilter.IsAllowed(tx, Build.A.BlockHeader.TestObject);
    }
}
