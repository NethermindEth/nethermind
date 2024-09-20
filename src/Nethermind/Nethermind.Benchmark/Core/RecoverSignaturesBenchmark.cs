using BenchmarkDotNet.Attributes;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Core;
using Nethermind.Crypto;
using Nethermind.Logging;
using Nethermind.Serialization.Rlp;
using Nethermind.Specs;
using System;
using System.Collections.Generic;
using Nethermind.Consensus.Processing;
using Nethermind.TxPool;
using Nethermind.Int256;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading.Tasks;

namespace Nethermind.Benchmarks.Core
{
    [MemoryDiagnoser]
    public class RecoverSignaturesBenchmark
    {
        private ISpecProvider _specProvider = MainnetSpecProvider.Instance;

        private static EthereumEcdsa _ethereumEcdsa;
        private static RecoverSignatures _sut;

        private Block _block100TxWith100AuthSigs;
        private Block _block100TxWith10AuthSigs;
        private Block _block100TxWith1AuthSigs;
        private Block _block3TxWith1AuthSigs;
        private Block _block10TxWith0AuthSigs;
        private Block _block10TxWith10AuthSigs;

        private static PrivateKey[] _privateKeys = Enumerable.Range(0, 1000)
            .Select(i => Build.A.PrivateKey.TestObject)
            .ToArray();

        [GlobalSetup]
        public void GlobalSetup()
        {
            _ethereumEcdsa = new(_specProvider.ChainId);
            _sut = new(_ethereumEcdsa, new AlwaysReturnNullTxPool(), _specProvider, NullLogManager.Instance);

            var rnd = new Random();

            _block100TxWith100AuthSigs = Build.A.Block
                .WithHeader(new BlockHeader()
                {
                    Timestamp = ulong.MaxValue,
                    Number = long.MaxValue
                })
                .WithTransactions(CreateTransactions(100, 100))
                .TestObject;
            _block100TxWith10AuthSigs = Build.A.Block
                .WithHeader(new BlockHeader()
                {
                    Timestamp = ulong.MaxValue,
                    Number = long.MaxValue
                })
                .WithTransactions(CreateTransactions(100, 10))
                .TestObject;

            _block100TxWith1AuthSigs = Build.A.Block
                .WithHeader(new BlockHeader()
                {
                    Timestamp = ulong.MaxValue,
                    Number = long.MaxValue
                })
                .WithTransactions(CreateTransactions(100, 1))
                .TestObject;

            _block10TxWith10AuthSigs = Build.A.Block
                .WithHeader(new BlockHeader()
                {
                    Timestamp = ulong.MaxValue,
                    Number = long.MaxValue
                })
                .WithTransactions(CreateTransactions(10, 10))
                .TestObject;

            _block3TxWith1AuthSigs = Build.A.Block
                .WithHeader(new BlockHeader()
                {
                    Timestamp = ulong.MaxValue,
                    Number = long.MaxValue
                })
                .WithTransactions(CreateTransactions(3, 1))
                .TestObject;

            _block10TxWith0AuthSigs = Build.A.Block
                .WithHeader(new BlockHeader()
                {
                    Timestamp = ulong.MaxValue,
                    Number = long.MaxValue
                })
                .WithTransactions(CreateTransactions(10, 0))
                .TestObject;

            Transaction[] CreateTransactions(int txCount, int authPerTx)
            {
                var list = new List<Transaction>();
                for (int i = 0; i < txCount; i++)
                {
                    PrivateKey signer = _privateKeys[i];

                    Transaction tx = Build.A.Transaction
                        .WithType(TxType.SetCode)
                        .WithAuthorizationCode(
                        Enumerable.Range(0, authPerTx).Select(y =>
                        {
                            PrivateKey authority = _privateKeys[i + y + _privateKeys.Length / 2];
                            return CreateAuthorizationTuple(
                            authority,
                            (ulong)rnd.NextInt64(),
                            Address.Zero,
                            (ulong)rnd.NextInt64());
                        }).ToArray()
                        )
                        .SignedAndResolved(signer)
                        .WithSenderAddress(null)
                        .TestObject;
                    list.Add(tx);
                }
                return list.ToArray();
            }

            static AuthorizationTuple CreateAuthorizationTuple(PrivateKey signer, ulong chainId, Address codeAddress, ulong nonce)
            {
                AuthorizationTupleDecoder decoder = new();
                RlpStream rlp = decoder.EncodeWithoutSignature(chainId, codeAddress, nonce);
                Span<byte> code = stackalloc byte[rlp.Length + 1];
                code[0] = Eip7702Constants.Magic;
                rlp.Data.AsSpan().CopyTo(code.Slice(1));

                Signature sig = _ethereumEcdsa.Sign(signer, Keccak.Compute(code));

                return new AuthorizationTuple(chainId, codeAddress, nonce, sig);
            }
        }

        [IterationCleanup]
        public void IterationCleanup()
        {
            ResetSigs(_block100TxWith100AuthSigs);
            ResetSigs(_block100TxWith10AuthSigs);
            ResetSigs(_block100TxWith1AuthSigs);
            ResetSigs(_block10TxWith10AuthSigs);
            ResetSigs(_block10TxWith0AuthSigs);
            ResetSigs(_block3TxWith1AuthSigs);

            void ResetSigs(Block block)
            {
                Parallel.ForEach(block.Transactions, (t)=>
                {
                    t.SenderAddress = null;
                    t.Hash = null;
                    Parallel.ForEach(t.AuthorizationList, (tuple) => {
                        tuple.Authority = null;
                    });
                });
            }
        }

        [Benchmark]
        public void Recover100TxSignatureswith100AuthoritySignatures()
        {
            _sut.RecoverData(_block100TxWith100AuthSigs);
        }

        [Benchmark]
        public void Recover100TxSignatureswith10AuthoritySignatures()
        {
            _sut.RecoverData(_block100TxWith10AuthSigs);
        }

        [Benchmark]
        public void Recover100TxSignaturesWith1AuthoritySignatures()
        {
            _sut.RecoverData(_block100TxWith1AuthSigs);
        }

        [Benchmark]
        public void Recover10TxSignaturesWith10AuthoritySignatures()
        {
            _sut.RecoverData(_block10TxWith10AuthSigs);
        }

        [Benchmark]
        public void Recover3TxSignaturesWith1AuthoritySignatures()
        {
            _sut.RecoverData(_block3TxWith1AuthSigs);
        }

        [Benchmark]
        public void Recover10TxSignaturesWith0AuthoritySignatures()
        {
            _sut.RecoverData(_block10TxWith0AuthSigs);
        }

        private class AlwaysReturnNullTxPool : ITxPool
        {
#pragma warning disable 67
            public event EventHandler<TxPool.TxEventArgs> NewDiscovered;
            public event EventHandler<TxPool.TxEventArgs> NewPending;
            public event EventHandler<TxPool.TxEventArgs> RemovedPending;
            public event EventHandler<TxPool.TxEventArgs> EvictedPending;
#pragma warning restore 67

            public void AddPeer(ITxPoolPeer peer)
            {
                throw new NotImplementedException();
            }

            public bool ContainsTx(Hash256 hash, TxType txType)
            {
                throw new NotImplementedException();
            }

            public Transaction GetBestTx()
            {
                throw new NotImplementedException();
            }

            public IEnumerable<Transaction> GetBestTxOfEachSender()
            {
                throw new NotImplementedException();
            }

            public UInt256 GetLatestPendingNonce(Address address)
            {
                throw new NotImplementedException();
            }

            public int GetPendingBlobTransactionsCount()
            {
                throw new NotImplementedException();
            }

            public IDictionary<AddressAsKey, Transaction[]> GetPendingLightBlobTransactionsBySender()
            {
                throw new NotImplementedException();
            }

            public Transaction[] GetPendingTransactions()
            {
                throw new NotImplementedException();
            }

            public IDictionary<AddressAsKey, Transaction[]> GetPendingTransactionsBySender()
            {
                throw new NotImplementedException();
            }

            public Transaction[] GetPendingTransactionsBySender(Address address)
            {
                throw new NotImplementedException();
            }

            public int GetPendingTransactionsCount()
            {
                throw new NotImplementedException();
            }

            public bool IsKnown(Hash256 hash)
            {
                throw new NotImplementedException();
            }

            public void RemovePeer(PublicKey nodeId)
            {
                throw new NotImplementedException();
            }

            public bool RemoveTransaction(Hash256 hash)
            {
                throw new NotImplementedException();
            }

            public AcceptTxResult SubmitTx(Transaction tx, TxHandlingOptions handlingOptions)
            {
                throw new NotImplementedException();
            }

            public bool TryGetBlobAndProof(byte[] blobVersionedHash, [NotNullWhen(true)] out byte[] blob, [NotNullWhen(true)] out byte[] proof)
            {
                throw new NotImplementedException();
            }

            public bool TryGetPendingBlobTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction blobTransaction)
            {
                throw new NotImplementedException();
            }

            public bool TryGetPendingTransaction(Hash256 hash, [NotNullWhen(true)] out Transaction transaction)
            {
                transaction = null;
                return false;
            }
        }
    }    
}
