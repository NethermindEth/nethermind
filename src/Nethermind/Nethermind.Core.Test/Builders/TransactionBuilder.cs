// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Crypto;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.Core.Test.Builders
{
    public class TransactionBuilder<T> : BuilderBase<T> where T : Transaction, new()
    {
        public TransactionBuilder()
        {
            TestObjectInternal = new T
            {
                GasPrice = 1,
                GasLimit = Transaction.BaseTxGasCost,
                To = Address.Zero,
                Nonce = 0,
                Value = 1,
                Data = Array.Empty<byte>(),
                Timestamp = 0,
            };
        }

        public TransactionBuilder<T> WithNonce(UInt256 nonce)
        {
            TestObjectInternal.Nonce = nonce;
            return this;
        }

        public TransactionBuilder<T> WithHash(Keccak? hash)
        {
            TestObjectInternal.Hash = hash;
            return this;
        }

        public TransactionBuilder<T> WithTo(Address? address)
        {
            TestObjectInternal.To = address;
            return this;
        }

        public TransactionBuilder<T> To(Address? address)
        {
            TestObjectInternal.To = address;
            return this;
        }

        public TransactionBuilder<T> WithData(byte[] data)
        {
            TestObjectInternal.Data = data;
            return this;
        }

        public TransactionBuilder<T> WithCode(byte[] data)
        {
            TestObjectInternal.Data = data;
            TestObjectInternal.To = null;
            return this;
        }

        public TransactionBuilder<T> WithChainId(ulong chainId)
        {
            TestObjectInternal.ChainId = chainId;
            return this;
        }

        public TransactionBuilder<T> WithGasPrice(UInt256 gasPrice)
        {
            TestObjectInternal.GasPrice = gasPrice;
            return this;
        }

        public TransactionBuilder<T> WithGasLimit(long gasLimit)
        {
            TestObjectInternal.GasLimit = gasLimit;
            return this;
        }

        public TransactionBuilder<T> WithMaxFeePerGas(UInt256 feeCap)
        {
            TestObjectInternal.DecodedMaxFeePerGas = feeCap;
            return this;
        }

        public TransactionBuilder<T> WithMaxFeePerGasIfSupports1559(UInt256 feeCap) =>
            TestObjectInternal.Supports1559 ? WithMaxFeePerGas(feeCap) : this;

        public TransactionBuilder<T> WithMaxPriorityFeePerGas(UInt256 maxPriorityFeePerGas)
        {
            TestObjectInternal.GasPrice = maxPriorityFeePerGas;
            return this;
        }

        public TransactionBuilder<T> WithGasBottleneck(UInt256 gasBottleneck)
        {
            TestObjectInternal.GasBottleneck = gasBottleneck;
            return this;
        }

        public TransactionBuilder<T> WithTimestamp(UInt256 timestamp)
        {
            TestObject.Timestamp = timestamp;
            return this;
        }

        public TransactionBuilder<T> WithValue(UInt256 value)
        {
            TestObjectInternal.Value = value;
            return this;
        }

        public TransactionBuilder<T> WithValue(int value)
        {
            TestObjectInternal.Value = (UInt256)value;
            return this;
        }

        public TransactionBuilder<T> WithAccessList(AccessList accessList)
        {
            TestObjectInternal.AccessList = accessList;
            TestObjectInternal.ChainId = TestObjectInternal.Signature?.ChainId ?? TestObjectInternal.ChainId;
            return this;
        }

        public TransactionBuilder<T> WithSenderAddress(Address? address)
        {
            TestObjectInternal.SenderAddress = address;
            return this;
        }

        public TransactionBuilder<T> WithMaxFeePerDataGas(UInt256? maxFeePerDataGas)
        {
            TestObjectInternal.MaxFeePerDataGas = maxFeePerDataGas;
            return this;
        }

        public TransactionBuilder<T> WithBlobVersionedHashes(byte[][]? blobVersionedHashes)
        {
            TestObjectInternal.BlobVersionedHashes = blobVersionedHashes;
            return this;
        }

        public TransactionBuilder<T> WithBlobVersionedHashes(int? count)
        {
            if (count is null)
            {
                return this;
            }

            TestObjectInternal.BlobVersionedHashes = Enumerable.Range(0, count.Value).Select(x =>
            {
                byte[] bvh = new byte[32];
                bvh[0] = KzgPolynomialCommitments.KzgBlobHashVersionV1;
                return bvh;
            }).ToArray();
            return this;
        }

        public TransactionBuilder<T> WithShardBlobTxTypeAndFieldsIfBlobTx(int blobCount = 1, bool isMempoolTx = true)
            => TestObjectInternal.Type == TxType.Blob ? WithShardBlobTxTypeAndFields(blobCount, isMempoolTx) : this;

        public TransactionBuilder<T> WithShardBlobTxTypeAndFields(int blobCount = 1, bool isMempoolTx = true)
        {
            if (blobCount is 0)
            {
                return this;
            }

            TestObjectInternal.Type = TxType.Blob;
            TestObjectInternal.MaxFeePerDataGas ??= 1;

            if (isMempoolTx)
            {
                TestObjectInternal.BlobVersionedHashes = new byte[blobCount][];
                ShardBlobNetworkWrapper wrapper = new(
                    blobs: new byte[blobCount][],
                    commitments: new byte[blobCount][],
                    proofs: new byte[blobCount][]
                    );

                for (int i = 0; i < blobCount; i++)
                {
                    TestObjectInternal.BlobVersionedHashes[i] = new byte[32];
                    wrapper.Blobs[i] = new byte[Ckzg.Ckzg.BytesPerBlob];
                    wrapper.Blobs[i][0] = (byte)(i % 256);
                    wrapper.Commitments[i] = new byte[Ckzg.Ckzg.BytesPerCommitment];
                    wrapper.Proofs[i] = new byte[Ckzg.Ckzg.BytesPerProof];

                    if (KzgPolynomialCommitments.IsInitialized)
                    {
                        KzgPolynomialCommitments.KzgifyBlob(
                            wrapper.Blobs[i],
                            wrapper.Commitments[i],
                            wrapper.Proofs[i],
                            TestObjectInternal.BlobVersionedHashes[i].AsSpan());
                    }
                    else
                    {
                        TestObjectInternal.BlobVersionedHashes[i]![0] = KzgPolynomialCommitments.KzgBlobHashVersionV1;
                        wrapper.Commitments[i][0] = (byte)(i % 256);
                        wrapper.Proofs[i][0] = (byte)(i % 256);
                    }
                }

                TestObjectInternal.NetworkWrapper = wrapper;
            }
            else
            {
                return WithBlobVersionedHashes(blobCount);
            }

            return this;
        }

        public TransactionBuilder<T> With(Action<T> anyChange)
        {
            anyChange(TestObjectInternal);
            return this;
        }

        public TransactionBuilder<T> WithSignature(Signature signature)
        {
            TestObjectInternal.Signature = signature;
            return this;
        }

        public TransactionBuilder<T> Signed(IEthereumEcdsa ecdsa, PrivateKey privateKey, bool isEip155Enabled = true)
        {
            ecdsa.Sign(privateKey, TestObjectInternal, isEip155Enabled);
            return this;
        }

        // TODO: auto create ecdsa here
        public TransactionBuilder<T> SignedAndResolved(IEthereumEcdsa ecdsa, PrivateKey privateKey, bool isEip155Enabled = true)
        {
            // make sure that you do not change anything in the tx after signing as this will lead to a different recovered address
            ecdsa.Sign(privateKey, TestObjectInternal, isEip155Enabled);
            TestObjectInternal.SenderAddress = privateKey.Address;
            return this;
        }

        public TransactionBuilder<T> SignedAndResolved(PrivateKey? privateKey = null)
        {
            privateKey ??= TestItem.IgnoredPrivateKey;
            EthereumEcdsa ecdsa = new(TestObjectInternal.ChainId ?? TestBlockchainIds.ChainId, LimboLogs.Instance);
            ecdsa.Sign(privateKey, TestObjectInternal, true);
            TestObjectInternal.SenderAddress = privateKey.Address;
            return this;
        }

        protected override void BeforeReturn()
        {
            base.BeforeReturn();
            if (TestObjectInternal.IsSigned)
            {
                TestObjectInternal.Hash = TestObjectInternal.CalculateHash();
            }
        }

        public TransactionBuilder<T> WithType(TxType txType)
        {
            TestObjectInternal.Type = txType;
            return this;
        }

        public TransactionBuilder<T> WithIsServiceTransaction(bool isServiceTransaction)
        {
            TestObjectInternal.IsServiceTransaction = isServiceTransaction;
            return this;
        }
    }
}
