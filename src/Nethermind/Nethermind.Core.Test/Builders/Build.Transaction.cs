// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Collections.Generic;
using System.Linq;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Extensions;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Core.Test.Builders
{
    public partial class Build
    {
        public TransactionBuilder<Transaction> Transaction => new();
        public TransactionBuilder<SystemTransaction> SystemTransaction => new();
        public TransactionBuilder<GeneratedTransaction> GeneratedTransaction => new();
        public TransactionBuilder<T> TypedTransaction<T>() where T : Transaction, new() => new();

        public TransactionBuilder<NamedTransaction> NamedTransaction(string name)
        {
            return new() { TestObjectInternal = { Name = name } };
        }
        public Transaction[] BunchOfTransactions(bool isSigned = true)
        {
            Address to = Build.An.Address.FromNumber(1).TestObject;
            byte[] blobData = new byte[4096 * 32];
            blobData[0] = 0xff;
            blobData[1] = 0x15;

            AccessList accessList = new AccessListBuilder()
                .AddAddress(new Address("0x1000000000000000000000000000000000000007"))
                .AddStorage(new UInt256(8))
                .AddStorage(new UInt256(8 << 25))
                .ToAccessList();

            TransactionBuilder<Transaction> simpleEmptyTx = Build.A.Transaction;
            TransactionBuilder<Transaction> simpleTx = Build.A.Transaction;

            TransactionBuilder<Transaction> networkBlobTx = Build.A.Transaction.WithTo(to).WithType(TxType.Blob)
                .WithChainId(1)
                .WithNonce(2)
                .WithGasLimit(3)
                .WithMaxFeePerGas(4)
                .WithGasPrice(5)
                .WithMaxFeePerDataGas(6)
                .WithValue(7)
                .WithTo(new Address("0xffb38a7a99e3e2335be83fc74b7faa19d5531243"))
                .WithAccessList(accessList)
                .WithData(new byte[] { 0x19, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 })
                .WithBlobVersionedHashes(new byte[][] { Bytes.FromHexString("0x018a3fa172b27f3240007eb2770906ff4f4c87bb0d2118e263a4f5ef94e4683c") })
                .WithBlobs(new byte[][] { blobData })
                .WithBlobKzgs(new byte[][] { Bytes.FromHexString("0xb46608161d1f715b8c838da2e4fb20e5a739b0a6bb41f27847f0535975c8b9430bf32bff9ffc91cb12480f1adb1d48a2") })
                .WithProof(Bytes.FromHexString("0x88033d1744e765ab78d5fa22af022bb5d3608dcf7c0b84515897f76b1d141a2054b2772eabbb8bd8979148164959bdc1"));

            IEnumerable<TransactionBuilder<Transaction>> txs = new TransactionBuilder<Transaction>[] {
                simpleEmptyTx,
                networkBlobTx,
            };

            if (isSigned)
            {
                txs = txs.Select(x => x.SignedAndResolved(new EthereumEcdsa(NetworkId.Ropsten, LimboLogs.Instance), TestItem.PrivateKeyA).WithSenderAddress(null));
            }

            return txs.Select(tx => tx.TestObject).ToArray();
        }
    }
}
