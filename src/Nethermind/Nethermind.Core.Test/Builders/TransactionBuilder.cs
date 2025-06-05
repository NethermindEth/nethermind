// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Linq;
using CkzgLib;
using Nethermind.Core.Crypto;
using Nethermind.Core.Eip2930;
using Nethermind.Core.Specs;
using Nethermind.Crypto;
using Nethermind.Int256;

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

        public TransactionBuilder<T> WithHash(Hash256? hash)
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

        public TransactionBuilder<T> WithChainId(ulong? chainId)
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

        public TransactionBuilder<T> WithAccessList(AccessList? accessList)
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

        public TransactionBuilder<T> WithMaxFeePerBlobGas(UInt256? maxFeePerBlobGas)
        {
            TestObjectInternal.MaxFeePerBlobGas = maxFeePerBlobGas;
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

            TestObjectInternal.BlobVersionedHashes = Enumerable.Range(0, count.Value).Select(_ =>
            {
                byte[] bvh = new byte[32];
                bvh[0] = KzgPolynomialCommitments.KzgBlobHashVersionV1;
                return bvh;
            }).ToArray();
            return this;
        }

        public TransactionBuilder<T> WithShardBlobTxTypeAndFieldsIfBlobTx(int blobCount = 1, bool isMempoolTx = true, IReleaseSpec? spec = null)
            => TestObjectInternal.Type == TxType.Blob ? WithShardBlobTxTypeAndFields(blobCount, isMempoolTx, spec) : this;

        public TransactionBuilder<T> WithShardBlobTxTypeAndFields(int blobCount = 1, bool isMempoolTx = true, IReleaseSpec? spec = null)
        {
            if (blobCount is 0)
            {
                return this;
            }

            TestObjectInternal.Type = TxType.Blob;
            TestObjectInternal.MaxFeePerBlobGas ??= 1;

            if (isMempoolTx)
            {
                IBlobProofsManager proofsManager = IBlobProofsManager.For(spec?.BlobProofVersion ?? ProofVersion.V0);

                ShardBlobNetworkWrapper wrapper = proofsManager.AllocateWrapper([.. Enumerable.Range(1, blobCount).Select(i =>
                {
                    byte[] blob = new byte[Ckzg.BytesPerBlob];
                    blob[0] = (byte)(i % 256);
                    return blob;
                })]);


                if (!KzgPolynomialCommitments.IsInitialized)
                {
                    KzgPolynomialCommitments.InitializeAsync().Wait();
                }

                proofsManager.ComputeProofsAndCommitments(wrapper);

                TestObjectInternal.BlobVersionedHashes = proofsManager.ComputeHashes(wrapper);
                TestObjectInternal.NetworkWrapper = wrapper;
            }
            else
            {
                return WithBlobVersionedHashes(blobCount);
            }

            return this;
        }

        public TransactionBuilder<T> WithAuthorizationCodeIfAuthorizationListTx()
        {
            return TestObjectInternal.Type == TxType.SetCode ? WithAuthorizationCode(new AuthorizationTuple(0, Address.Zero, 0, new Signature(new byte[64], 0))) : this;
        }

        public TransactionBuilder<T> WithAuthorizationCode(AuthorizationTuple authTuple)
        {
            TestObjectInternal.AuthorizationList = TestObjectInternal.AuthorizationList is not null ? [.. TestObjectInternal.AuthorizationList, authTuple] : [authTuple];
            return this;
        }
        public TransactionBuilder<T> WithAuthorizationCode(AuthorizationTuple[] authList)
        {
            TestObjectInternal.AuthorizationList = authList;
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

        public TransactionBuilder<T> Signed(PrivateKey? privateKey = null, bool isEip155Enabled = true)
        {
            privateKey ??= TestItem.IgnoredPrivateKey;
            EthereumEcdsa ecdsa = new(TestObjectInternal.ChainId ?? TestBlockchainIds.ChainId);

            return Signed(ecdsa, privateKey, isEip155Enabled);
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
            EthereumEcdsa ecdsa = new(TestObjectInternal.ChainId ?? TestBlockchainIds.ChainId);
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

        public TransactionBuilder<T> WithSourceHash(Hash256? sourceHash)
        {
            TestObjectInternal.SourceHash = sourceHash;
            return this;
        }

        public TransactionBuilder<T> WithIsOPSystemTransaction(bool isOPSystemTransaction)
        {
            TestObjectInternal.IsOPSystemTransaction = isOPSystemTransaction;
            return this;
        }

        public TransactionBuilder<T> From(T item)
        {
            TestObjectInternal = item;
            return this;
        }
    }
}
