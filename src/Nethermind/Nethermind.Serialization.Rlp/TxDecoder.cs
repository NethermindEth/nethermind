// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using FastEnumUtility;
using Microsoft.Extensions.ObjectPool;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Serialization.Rlp.Eip2930;

namespace Nethermind.Serialization.Rlp
{
    public class TxDecoder : TxDecoder<Transaction>
    {
        public const int MaxDelayedHashTxnSize = 32768;
        public static TxDecoder Instance = new TxDecoder();
        public static TxDecoder InstanceWithoutLazyHash = new TxDecoder(false);
        public static ObjectPool<Transaction> TxObjectPool = new DefaultObjectPool<Transaction>(new Transaction.PoolPolicy(), Environment.ProcessorCount * 4);

        public TxDecoder() : base(true) // Rlp will try to find empty constructor.
        {
        }

        public TxDecoder(bool lazyHash) : base(lazyHash)
        {
        }

        protected override Transaction NewTx()
        {
            return TxObjectPool.Get();
        }
    }
    public class SystemTxDecoder : TxDecoder<SystemTransaction> { }
    public class GeneratedTxDecoder : TxDecoder<GeneratedTransaction> { }

    public class TxDecoder<T> :
        IRlpStreamDecoder<T>,
        IRlpValueDecoder<T>
        where T : Transaction, new()
    {
        private readonly AccessListDecoder _accessListDecoder = new();
        private readonly bool _lazyHash;

        protected TxDecoder(bool lazyHash = true)
        {
            _lazyHash = lazyHash;
        }

        protected virtual T NewTx()
        {
            return new();
        }

        public T? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            Span<byte> transactionSequence = DecodeTxTypeAndGetSequence(rlpStream, rlpBehaviors, out TxType txType);

            T transaction = NewTx();
            transaction.Type = txType;

            int positionAfterNetworkWrapper = 0;
            if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm && transaction.MayHaveNetworkForm)
            {
                int networkWrapperLength = rlpStream.ReadSequenceLength();
                positionAfterNetworkWrapper = rlpStream.Position + networkWrapperLength;
                int rlpLength = rlpStream.PeekNextRlpLength();
                transactionSequence = rlpStream.Peek(rlpLength);
            }

            int transactionLength = rlpStream.ReadSequenceLength();
            int lastCheck = rlpStream.Position + transactionLength;

            switch (transaction.Type)
            {
                case TxType.Legacy:
                    TxDecoder<T>.DecodeLegacyPayloadWithoutSig(transaction, rlpStream);
                    break;
                case TxType.AccessList:
                    DecodeAccessListPayloadWithoutSig(transaction, rlpStream, rlpBehaviors);
                    break;
                case TxType.EIP1559:
                    DecodeEip1559PayloadWithoutSig(transaction, rlpStream, rlpBehaviors);
                    break;
                case TxType.Blob:
                    DecodeShardBlobPayloadWithoutSig(transaction, rlpStream, rlpBehaviors);
                    break;
                case TxType.DepositTx:
                    TxDecoder<T>.DecodeDepositPayloadWithoutSig(transaction, rlpStream, rlpBehaviors);
                    break;
            }

            if (rlpStream.Position < lastCheck)
            {
                DecodeSignature(rlpStream, rlpBehaviors, transaction);
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                rlpStream.Check(lastCheck);
            }

            if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm && transaction.MayHaveNetworkForm)
            {
                switch (transaction.Type)
                {
                    case TxType.Blob:
                        TxDecoder<T>.DecodeShardBlobNetworkPayload(transaction, rlpStream, rlpBehaviors);
                        break;
                }

                if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
                {
                    rlpStream.Check(positionAfterNetworkWrapper);
                }

                transaction.Hash = CalculateHashForNetworkPayloadForm(transaction.Type, transactionSequence);
            }
            else if (transactionSequence.Length <= TxDecoder.MaxDelayedHashTxnSize && _lazyHash)
            {
                // Delay hash generation, as may be filtered as having too low gas etc
                transaction.SetPreHashNoLock(transactionSequence);
            }
            else
            {
                // Just calculate the Hash as txn too large
                transaction.Hash = Keccak.Compute(transactionSequence);
            }

            return transaction;
        }

        private static Span<byte> DecodeTxTypeAndGetSequence(RlpStream rlpStream, RlpBehaviors rlpBehaviors, out TxType txType)
        {
            static Span<byte> DecodeTxType(RlpStream rlpStream, int length, out TxType txType)
            {
                Span<byte> sequence = rlpStream.Peek(length);
                txType = (TxType)rlpStream.ReadByte();
                return txType == TxType.Legacy
                    ? throw new RlpException("Legacy transactions are not allowed in EIP-2718 Typed Transaction Envelope.")
                    : sequence;
            }

            Span<byte> transactionSequence = rlpStream.PeekNextItem();
            txType = TxType.Legacy;
            if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping)
            {
                byte firstByte = rlpStream.PeekByte();
                if (firstByte <= 0x7f) // it is typed transactions
                {
                    transactionSequence = DecodeTxType(rlpStream, rlpStream.Length, out txType);
                }
            }
            else if (!rlpStream.IsSequenceNext())
            {
                transactionSequence = DecodeTxType(rlpStream, rlpStream.ReadPrefixAndContentLength().ContentLength, out txType);
            }

            return transactionSequence;
        }

        private static Hash256 CalculateHashForNetworkPayloadForm(TxType type, Span<byte> transactionSequence)
        {
            KeccakHash hash = KeccakHash.Create();
            Span<byte> txType = stackalloc byte[1];
            txType[0] = (byte)type;
            hash.Update(txType);
            hash.Update(transactionSequence);
            return new Hash256(hash.Hash);
        }

        private static void DecodeLegacyPayloadWithoutSig(T transaction, RlpStream rlpStream)
        {
            transaction.Nonce = rlpStream.DecodeUInt256();
            transaction.GasPrice = rlpStream.DecodeUInt256();
            transaction.GasLimit = rlpStream.DecodeLong();
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256();
            transaction.Data = rlpStream.DecodeByteArray();
        }

        private void DecodeAccessListPayloadWithoutSig(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = rlpStream.DecodeULong();
            transaction.Nonce = rlpStream.DecodeUInt256();
            transaction.GasPrice = rlpStream.DecodeUInt256();
            transaction.GasLimit = rlpStream.DecodeLong();
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256();
            transaction.Data = rlpStream.DecodeByteArray();
            transaction.AccessList = _accessListDecoder.Decode(rlpStream, rlpBehaviors);
        }

        private void DecodeEip1559PayloadWithoutSig(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = rlpStream.DecodeULong();
            transaction.Nonce = rlpStream.DecodeUInt256();
            transaction.GasPrice = rlpStream.DecodeUInt256(); // gas premium
            transaction.DecodedMaxFeePerGas = rlpStream.DecodeUInt256();
            transaction.GasLimit = rlpStream.DecodeLong();
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256();
            transaction.Data = rlpStream.DecodeByteArray();
            transaction.AccessList = _accessListDecoder.Decode(rlpStream, rlpBehaviors);
        }

        private void DecodeShardBlobPayloadWithoutSig(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = rlpStream.DecodeULong();
            transaction.Nonce = rlpStream.DecodeUInt256();
            transaction.GasPrice = rlpStream.DecodeUInt256(); // gas premium
            transaction.DecodedMaxFeePerGas = rlpStream.DecodeUInt256();
            transaction.GasLimit = rlpStream.DecodeLong();
            transaction.To = rlpStream.DecodeAddress();
            transaction.Value = rlpStream.DecodeUInt256();
            transaction.Data = rlpStream.DecodeByteArray();
            transaction.AccessList = _accessListDecoder.Decode(rlpStream, rlpBehaviors);
            transaction.MaxFeePerBlobGas = rlpStream.DecodeUInt256();
            transaction.BlobVersionedHashes = rlpStream.DecodeByteArrays();
        }

        private static void DecodeShardBlobNetworkPayload(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
        {
            byte[][] blobs = rlpStream.DecodeByteArrays();
            byte[][] commitments = rlpStream.DecodeByteArrays();
            byte[][] proofs = rlpStream.DecodeByteArrays();
            transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs);
        }

        private static void DecodeDepositPayloadWithoutSig(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
        {
            transaction.SourceHash = rlpStream.DecodeKeccak();
            transaction.SenderAddress = rlpStream.DecodeAddress();
            transaction.To = rlpStream.DecodeAddress();
            transaction.Mint = rlpStream.DecodeUInt256();
            transaction.Value = rlpStream.DecodeUInt256();
            transaction.GasLimit = rlpStream.DecodeLong();
            transaction.IsOPSystemTransaction = rlpStream.DecodeBool();
            transaction.Data = rlpStream.DecodeByteArray();
        }

        private static void DecodeLegacyPayloadWithoutSig(T transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
        {
            transaction.Nonce = decoderContext.DecodeUInt256();
            transaction.GasPrice = decoderContext.DecodeUInt256();
            transaction.GasLimit = decoderContext.DecodeLong();
            transaction.To = decoderContext.DecodeAddress();
            transaction.Value = decoderContext.DecodeUInt256();
            transaction.Data = decoderContext.DecodeByteArrayMemory();
        }

        private void DecodeAccessListPayloadWithoutSig(T transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = decoderContext.DecodeULong();
            transaction.Nonce = decoderContext.DecodeUInt256();
            transaction.GasPrice = decoderContext.DecodeUInt256();
            transaction.GasLimit = decoderContext.DecodeLong();
            transaction.To = decoderContext.DecodeAddress();
            transaction.Value = decoderContext.DecodeUInt256();
            transaction.Data = decoderContext.DecodeByteArrayMemory();
            transaction.AccessList = _accessListDecoder.Decode(ref decoderContext, rlpBehaviors);
        }

        private void DecodeEip1559PayloadWithoutSig(T transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = decoderContext.DecodeULong();
            transaction.Nonce = decoderContext.DecodeUInt256();
            transaction.GasPrice = decoderContext.DecodeUInt256(); // gas premium
            transaction.DecodedMaxFeePerGas = decoderContext.DecodeUInt256();
            transaction.GasLimit = decoderContext.DecodeLong();
            transaction.To = decoderContext.DecodeAddress();
            transaction.Value = decoderContext.DecodeUInt256();
            transaction.Data = decoderContext.DecodeByteArrayMemory();
            transaction.AccessList = _accessListDecoder.Decode(ref decoderContext, rlpBehaviors);
        }

        private void DecodeShardBlobPayloadWithoutSig(T transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
        {
            transaction.ChainId = decoderContext.DecodeULong();
            transaction.Nonce = decoderContext.DecodeUInt256();
            transaction.GasPrice = decoderContext.DecodeUInt256(); // gas premium
            transaction.DecodedMaxFeePerGas = decoderContext.DecodeUInt256();
            transaction.GasLimit = decoderContext.DecodeLong();
            transaction.To = decoderContext.DecodeAddress();
            transaction.Value = decoderContext.DecodeUInt256();
            transaction.Data = decoderContext.DecodeByteArray();
            transaction.AccessList = _accessListDecoder.Decode(ref decoderContext, rlpBehaviors);
            transaction.MaxFeePerBlobGas = decoderContext.DecodeUInt256();
            transaction.BlobVersionedHashes = decoderContext.DecodeByteArrays();
        }

        private static void DecodeShardBlobNetworkPayload(T transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
        {
            byte[][] blobs = decoderContext.DecodeByteArrays();
            byte[][] commitments = decoderContext.DecodeByteArrays();
            byte[][] proofs = decoderContext.DecodeByteArrays();
            transaction.NetworkWrapper = new ShardBlobNetworkWrapper(blobs, commitments, proofs);
        }

        private static void DecodeDepositPayloadWithoutSig(T transaction, ref Rlp.ValueDecoderContext decoderContext, RlpBehaviors rlpBehaviors)
        {
            transaction.SourceHash = decoderContext.DecodeKeccak();
            transaction.SenderAddress = decoderContext.DecodeAddress();
            transaction.To = decoderContext.DecodeAddress();
            transaction.Mint = decoderContext.DecodeUInt256();
            transaction.Value = decoderContext.DecodeUInt256();
            transaction.GasLimit = decoderContext.DecodeLong();
            transaction.IsOPSystemTransaction = decoderContext.DecodeBool();
            transaction.Data = decoderContext.DecodeByteArray();
        }

        private static void EncodeLegacyWithoutPayload(T item, RlpStream stream)
        {
            stream.Encode(item.Nonce);
            stream.Encode(item.GasPrice);
            stream.Encode(item.GasLimit);
            stream.Encode(item.To);
            stream.Encode(item.Value);
            stream.Encode(item.Data);
        }

        private void EncodeAccessListPayloadWithoutPayload(T item, RlpStream stream, RlpBehaviors rlpBehaviors)
        {
            stream.Encode(item.ChainId ?? 0);
            stream.Encode(item.Nonce);
            stream.Encode(item.GasPrice);
            stream.Encode(item.GasLimit);
            stream.Encode(item.To);
            stream.Encode(item.Value);
            stream.Encode(item.Data);
            _accessListDecoder.Encode(stream, item.AccessList, rlpBehaviors);
        }

        private void EncodeEip1559PayloadWithoutPayload(T item, RlpStream stream, RlpBehaviors rlpBehaviors)
        {
            stream.Encode(item.ChainId ?? 0);
            stream.Encode(item.Nonce);
            stream.Encode(item.GasPrice); // gas premium
            stream.Encode(item.DecodedMaxFeePerGas);
            stream.Encode(item.GasLimit);
            stream.Encode(item.To);
            stream.Encode(item.Value);
            stream.Encode(item.Data);
            _accessListDecoder.Encode(stream, item.AccessList, rlpBehaviors);
        }

        private void EncodeShardBlobPayloadWithoutPayload(T item, RlpStream stream, RlpBehaviors rlpBehaviors)
        {
            stream.Encode(item.ChainId ?? 0);
            stream.Encode(item.Nonce);
            stream.Encode(item.GasPrice); // gas premium
            stream.Encode(item.DecodedMaxFeePerGas);
            stream.Encode(item.GasLimit);
            stream.Encode(item.To);
            stream.Encode(item.Value);
            stream.Encode(item.Data);
            _accessListDecoder.Encode(stream, item.AccessList, rlpBehaviors);
            stream.Encode(item.MaxFeePerBlobGas.Value);
            stream.Encode(item.BlobVersionedHashes);
        }

        private static void EncodeDepositTxPayloadWithoutPayload(T item, RlpStream stream)
        {
            stream.Encode(item.SourceHash);
            stream.Encode(item.SenderAddress);
            stream.Encode(item.To);
            stream.Encode(item.Mint);
            stream.Encode(item.Value);
            stream.Encode(item.GasLimit);
            stream.Encode(item.IsOPSystemTransaction);
            stream.Encode(item.Data);
        }

        // b9018201f9017e86796f6c6f763304843b9aca00829ab0948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f90111f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a09e41e382c76d2913521d7191ecced4a1a16fe0f3e5e22d83d50dd58adbe409e1a07c0e036eff80f9ca192ac26d533fc49c280d90c8b62e90c1a1457b50e51e6144
        // b8__ca01f8__c786796f6c6f763304843b9aca00829ab0948a8eafb1cf62bfbeb1741769dae1a9dd479961928080f8__5bf859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a00000000000000000000000000000000000000133700000000000000000000000f859940000000000000000000000000000000000001337f842a00000000000000000000000000000000000000000000000000000000000000000a0000000000000000000000000000000000000013370000000000000000000000080a09e41e382c76d2913521d7191ecced4a1a16fe0f3e5e22d83d50dd58adbe409e1a07c0e036eff80f9ca192ac26d533fc49c280d90c8b62e90c1a1457b50e51e6144000000

        public T? Decode(ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)

        {
            T transaction = null;
            Decode(ref decoderContext, ref transaction, rlpBehaviors);

            return transaction;
        }


        public void Decode(ref Rlp.ValueDecoderContext decoderContext, ref T? transaction,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                decoderContext.ReadByte();
                transaction = null;
                return;
            }

            transaction ??= NewTx();
            transaction.Type = TxType.Legacy;

            int txSequenceStart = decoderContext.Position;
            Span<byte> transactionSequence = decoderContext.PeekNextItem();

            if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping)
            {
                byte firstByte = decoderContext.PeekByte();
                if (firstByte <= 0x7f) // it is typed transactions
                {
                    txSequenceStart = decoderContext.Position;
                    transactionSequence = decoderContext.Peek(decoderContext.Length);
                    transaction.Type = (TxType)decoderContext.ReadByte();
                }
            }
            else
            {
                if (!decoderContext.IsSequenceNext())
                {
                    (int PrefixLength, int ContentLength) prefixAndContentLength =
                        decoderContext.ReadPrefixAndContentLength();
                    txSequenceStart = decoderContext.Position;
                    transactionSequence = decoderContext.Peek(prefixAndContentLength.ContentLength);
                    transaction.Type = (TxType)decoderContext.ReadByte();
                }
            }

            int networkWrapperCheck = 0;
            if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm && transaction.MayHaveNetworkForm)
            {
                int networkWrapperLength = decoderContext.ReadSequenceLength();
                networkWrapperCheck = decoderContext.Position + networkWrapperLength;
                int rlpRength = decoderContext.PeekNextRlpLength();
                txSequenceStart = decoderContext.Position;
                transactionSequence = decoderContext.Peek(rlpRength);
            }

            int transactionLength = decoderContext.ReadSequenceLength();
            int lastCheck = decoderContext.Position + transactionLength;

            switch (transaction.Type)
            {
                case TxType.Legacy:
                    TxDecoder<T>.DecodeLegacyPayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);
                    break;
                case TxType.AccessList:
                    DecodeAccessListPayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);
                    break;
                case TxType.EIP1559:
                    DecodeEip1559PayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);
                    break;
                case TxType.Blob:
                    DecodeShardBlobPayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);
                    break;
                case TxType.DepositTx:
                    TxDecoder<T>.DecodeDepositPayloadWithoutSig(transaction, ref decoderContext, rlpBehaviors);
                    break;
            }

            if (decoderContext.Position < lastCheck)
            {
                DecodeSignature(ref decoderContext, rlpBehaviors, transaction);
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(lastCheck);
            }

            if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm && transaction.MayHaveNetworkForm)
            {
                switch (transaction.Type)
                {
                    case TxType.Blob:
                        TxDecoder<T>.DecodeShardBlobNetworkPayload(transaction, ref decoderContext, rlpBehaviors);
                        break;
                }

                if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
                {
                    decoderContext.Check(networkWrapperCheck);
                }

                transaction.Hash = CalculateHashForNetworkPayloadForm(transaction.Type, transactionSequence);
            }
            else if (transactionSequence.Length <= TxDecoder.MaxDelayedHashTxnSize && _lazyHash)
            {
                // Delay hash generation, as may be filtered as having too low gas etc
                if (decoderContext.ShouldSliceMemory)
                {
                    // Do not copy the memory in this case.
                    int currentPosition = decoderContext.Position;
                    decoderContext.Position = txSequenceStart;
                    transaction.SetPreHashMemoryNoLock(decoderContext.ReadMemory(transactionSequence.Length));
                    decoderContext.Position = currentPosition;
                }
                else
                {
                    transaction.SetPreHashNoLock(transactionSequence);
                }
            }
            else
            {
                // Just calculate the Hash immediately as txn too large
                transaction.Hash = Keccak.Compute(transactionSequence);
            }
        }

        private static void DecodeSignature(
            RlpStream rlpStream,
            RlpBehaviors rlpBehaviors,
            T transaction)
        {
            ulong v = rlpStream.DecodeULong();
            ReadOnlySpan<byte> rBytes = rlpStream.DecodeByteArraySpan();
            ReadOnlySpan<byte> sBytes = rlpStream.DecodeByteArraySpan();
            ApplySignature(transaction, v, rBytes, sBytes, rlpBehaviors);
        }

        private static void DecodeSignature(
            ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors,
            T transaction)
        {
            ulong v = decoderContext.DecodeULong();
            ReadOnlySpan<byte> rBytes = decoderContext.DecodeByteArraySpan();
            ReadOnlySpan<byte> sBytes = decoderContext.DecodeByteArraySpan();
            ApplySignature(transaction, v, rBytes, sBytes, rlpBehaviors);
        }

        private static void ApplySignature(
            T transaction,
            ulong v,
            ReadOnlySpan<byte> rBytes,
            ReadOnlySpan<byte> sBytes,
            RlpBehaviors rlpBehaviors)
        {
            if (transaction.Type == TxType.DepositTx && v == 0 && rBytes.IsEmpty && sBytes.IsEmpty) return;

            bool allowUnsigned = (rlpBehaviors & RlpBehaviors.AllowUnsigned) == RlpBehaviors.AllowUnsigned;
            bool isSignatureOk = true;
            string signatureError = null;
            if (rBytes == null || sBytes == null)
            {
                isSignatureOk = false;
                signatureError = "VRS null when decoding Transaction";
            }
            else if (rBytes.Length == 0 || sBytes.Length == 0)
            {
                isSignatureOk = false;
                signatureError = "VRS is 0 length when decoding Transaction";
            }
            else if (rBytes[0] == 0 || sBytes[0] == 0)
            {
                isSignatureOk = false;
                signatureError = "VRS starting with 0";
            }
            else if (rBytes.Length > 32 || sBytes.Length > 32)
            {
                isSignatureOk = false;
                signatureError = "R and S lengths expected to be less or equal 32";
            }
            else if (rBytes.SequenceEqual(Bytes.Zero32) && sBytes.SequenceEqual(Bytes.Zero32))
            {
                isSignatureOk = false;
                signatureError = "Both 'r' and 's' are zero when decoding a transaction.";
            }

            if (isSignatureOk)
            {
                if (transaction.Type != TxType.Legacy)
                {
                    v += Signature.VOffset;
                }

                Signature signature = new(rBytes, sBytes, v);
                transaction.Signature = signature;
            }
            else if (!allowUnsigned)
            {
                throw new RlpException(signatureError);
            }
        }

        public Rlp Encode(T item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);
            return new Rlp(rlpStream.Data);
        }

        public void Encode(RlpStream stream, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            EncodeTx(stream, item, rlpBehaviors);
        }

        public Rlp EncodeTx(T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
            bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
        {
            RlpStream rlpStream = new(GetTxLength(item, rlpBehaviors, forSigning, isEip155Enabled, chainId));
            EncodeTx(rlpStream, item, rlpBehaviors, forSigning, isEip155Enabled, chainId);
            return new Rlp(rlpStream.Data);
        }

        private void EncodeTx(RlpStream stream, T? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None,
            bool forSigning = false, bool isEip155Enabled = false, ulong chainId = 0)
        {
            if (item is null)
            {
                stream.WriteByte(Rlp.NullObjectByte);
                return;
            }

            bool includeSigChainIdHack = isEip155Enabled && chainId != 0 && item.Type == TxType.Legacy;

            int contentLength = GetContentLength(item, forSigning, isEip155Enabled, chainId,
                (rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm);
            int sequenceLength = Rlp.LengthOfSequence(contentLength);

            if (item.Type != TxType.Legacy)
            {
                if ((rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.None)
                {
                    stream.StartByteArray(sequenceLength + 1, false);
                }

                stream.WriteByte((byte)item.Type);
            }

            if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm && item.MayHaveNetworkForm)
            {
                stream.StartSequence(contentLength);
                contentLength = GetContentLength(item, forSigning, isEip155Enabled, chainId, false);
            }

            stream.StartSequence(contentLength);

            switch (item.Type)
            {
                case TxType.Legacy:
                    TxDecoder<T>.EncodeLegacyWithoutPayload(item, stream);
                    break;
                case TxType.AccessList:
                    EncodeAccessListPayloadWithoutPayload(item, stream, rlpBehaviors);
                    break;
                case TxType.EIP1559:
                    EncodeEip1559PayloadWithoutPayload(item, stream, rlpBehaviors);
                    break;
                case TxType.Blob:
                    EncodeShardBlobPayloadWithoutPayload(item, stream, rlpBehaviors);
                    break;
                case TxType.DepositTx:
                    TxDecoder<T>.EncodeDepositTxPayloadWithoutPayload(item, stream);
                    break;
            }

            EncodeSignature(stream, item, forSigning, chainId, includeSigChainIdHack);

            if ((rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm && item.MayHaveNetworkForm)
            {
                switch (item.Type)
                {
                    case TxType.Blob:
                        TxDecoder<T>.EncodeShardBlobNetworkPayload(item, stream, rlpBehaviors);
                        break;
                }
            }
        }

        private static void EncodeSignature(RlpStream stream, T item, bool forSigning, ulong chainId, bool includeSigChainIdHack)
        {
            if (item.Type == TxType.DepositTx)
                return;

            if (forSigning)
            {
                if (includeSigChainIdHack)
                {
                    stream.Encode(chainId);
                    stream.Encode(Rlp.OfEmptyByteArray);
                    stream.Encode(Rlp.OfEmptyByteArray);
                }
            }
            else
            {
                // TODO: move it to a signature decoder
                if (item.Signature is null)
                {
                    stream.Encode(0);
                    stream.Encode(Bytes.Empty);
                    stream.Encode(Bytes.Empty);
                }
                else
                {
                    stream.Encode(item.Type == TxType.Legacy ? item.Signature.V : item.Signature.RecoveryId);
                    stream.Encode(item.Signature.RAsSpan.WithoutLeadingZeros());
                    stream.Encode(item.Signature.SAsSpan.WithoutLeadingZeros());
                }
            }
        }

        private static void EncodeShardBlobNetworkPayload(T transaction, RlpStream rlpStream, RlpBehaviors rlpBehaviors)
        {
            ShardBlobNetworkWrapper networkWrapper = transaction.NetworkWrapper as ShardBlobNetworkWrapper;
            rlpStream.Encode(networkWrapper.Blobs);
            rlpStream.Encode(networkWrapper.Commitments);
            rlpStream.Encode(networkWrapper.Proofs);
        }

        private static int GetLegacyContentLength(T item)
        {
            return Rlp.LengthOf(item.Nonce)
                + Rlp.LengthOf(item.GasPrice)
                + Rlp.LengthOf(item.GasLimit)
                + Rlp.LengthOf(item.To)
                + Rlp.LengthOf(item.Value)
                + Rlp.LengthOf(item.Data);
        }

        private int GetAccessListContentLength(T item)
        {
            return Rlp.LengthOf(item.Nonce)
                   + Rlp.LengthOf(item.GasPrice)
                   + Rlp.LengthOf(item.GasLimit)
                   + Rlp.LengthOf(item.To)
                   + Rlp.LengthOf(item.Value)
                   + Rlp.LengthOf(item.Data)
                   + Rlp.LengthOf(item.ChainId ?? 0)
                   + _accessListDecoder.GetLength(item.AccessList, RlpBehaviors.None);
        }

        private int GetEip1559ContentLength(T item)
        {
            return Rlp.LengthOf(item.Nonce)
                   + Rlp.LengthOf(item.GasPrice) // gas premium
                   + Rlp.LengthOf(item.DecodedMaxFeePerGas)
                   + Rlp.LengthOf(item.GasLimit)
                   + Rlp.LengthOf(item.To)
                   + Rlp.LengthOf(item.Value)
                   + Rlp.LengthOf(item.Data)
                   + Rlp.LengthOf(item.ChainId ?? 0)
                   + _accessListDecoder.GetLength(item.AccessList, RlpBehaviors.None);
        }

        private int GetShardBlobContentLength(T item)
        {
            return Rlp.LengthOf(item.Nonce)
                   + Rlp.LengthOf(item.GasPrice) // gas premium
                   + Rlp.LengthOf(item.DecodedMaxFeePerGas)
                   + Rlp.LengthOf(item.GasLimit)
                   + Rlp.LengthOf(item.To)
                   + Rlp.LengthOf(item.Value)
                   + Rlp.LengthOf(item.Data)
                   + Rlp.LengthOf(item.ChainId ?? 0)
                   + _accessListDecoder.GetLength(item.AccessList, RlpBehaviors.None)
                   + Rlp.LengthOf(item.MaxFeePerBlobGas)
                   + Rlp.LengthOf(item.BlobVersionedHashes);
        }

        private static int GetShardBlobNetworkWrapperContentLength(T item, int txContentLength)
        {
            ShardBlobNetworkWrapper networkWrapper = item.NetworkWrapper as ShardBlobNetworkWrapper;
            return Rlp.LengthOfSequence(txContentLength)
                   + Rlp.LengthOf(networkWrapper.Blobs)
                   + Rlp.LengthOf(networkWrapper.Commitments)
                   + Rlp.LengthOf(networkWrapper.Proofs);
        }

        private static int GetDepositTxContentLength(T item)
        {
            return Rlp.LengthOf(item.SourceHash)
                   + Rlp.LengthOf(item.SenderAddress)
                   + Rlp.LengthOf(item.To)
                   + Rlp.LengthOf(item.Mint)
                   + Rlp.LengthOf(item.Value)
                   + Rlp.LengthOf(item.GasLimit)
                   + Rlp.LengthOf(item.IsOPSystemTransaction)
                   + Rlp.LengthOf(item.Data);
        }

        private int GetContentLength(T item, bool forSigning, bool isEip155Enabled = false, ulong chainId = 0,
            bool withNetworkWrapper = false)
        {
            bool includeSigChainIdHack = isEip155Enabled && chainId != 0 && item.Type == TxType.Legacy;
            int contentLength = 0;
            switch (item.Type)
            {
                case TxType.Legacy:
                    contentLength = TxDecoder<T>.GetLegacyContentLength(item);
                    break;
                case TxType.AccessList:
                    contentLength = GetAccessListContentLength(item);
                    break;
                case TxType.EIP1559:
                    contentLength = GetEip1559ContentLength(item);
                    break;
                case TxType.Blob:
                    contentLength = GetShardBlobContentLength(item);
                    break;
                case TxType.DepositTx:
                    contentLength = TxDecoder<T>.GetDepositTxContentLength(item);
                    break;
            }

            contentLength += GetSignatureContentLength(item, forSigning, chainId, includeSigChainIdHack);

            if (withNetworkWrapper)
            {
                if (item.Type == TxType.Blob)
                {
                    contentLength = TxDecoder<T>.GetShardBlobNetworkWrapperContentLength(item, contentLength);
                }
            }
            return contentLength;
        }

        private static int GetSignatureContentLength(T item, bool forSigning, ulong chainId, bool includeSigChainIdHack)
        {
            if (item.Type == TxType.DepositTx)
                return 0;

            int contentLength = 0;

            if (forSigning)
            {
                if (includeSigChainIdHack)
                {
                    contentLength += Rlp.LengthOf(chainId);
                    contentLength += 1;
                    contentLength += 1;
                }
            }
            else
            {
                bool signatureIsNull = item.Signature == null;
                contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Type == TxType.Legacy ? item.Signature.V : item.Signature.RecoveryId);
                contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Signature.RAsSpan.WithoutLeadingZeros());
                contentLength += signatureIsNull ? 1 : Rlp.LengthOf(item.Signature.SAsSpan.WithoutLeadingZeros());
            }

            return contentLength;
        }

        /// <summary>
        /// https://eips.ethereum.org/EIPS/eip-2718
        /// </summary>
        public int GetLength(T tx, RlpBehaviors rlpBehaviors)
        {
            int txContentLength = GetContentLength(tx, false,
                withNetworkWrapper: (rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm);
            int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

            bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
            int result = tx.Type != TxType.Legacy
                ? isForTxRoot
                    ? (1 + txPayloadLength)
                    : Rlp.LengthOfSequence(1 + txPayloadLength) // Rlp(TransactionType || TransactionPayload)
                : txPayloadLength;
            return result;
        }

        private int GetTxLength(T tx, RlpBehaviors rlpBehaviors, bool forSigning = false, bool isEip155Enabled = false,
            ulong chainId = 0)
        {
            int txContentLength = GetContentLength(tx, forSigning, isEip155Enabled, chainId,
               (rlpBehaviors & RlpBehaviors.InMempoolForm) == RlpBehaviors.InMempoolForm);
            int txPayloadLength = Rlp.LengthOfSequence(txContentLength);

            bool isForTxRoot = (rlpBehaviors & RlpBehaviors.SkipTypedWrapping) == RlpBehaviors.SkipTypedWrapping;
            int result = tx.Type != TxType.Legacy
                ? isForTxRoot
                    ? (1 + txPayloadLength)
                    : Rlp.LengthOfSequence(1 + txPayloadLength) // Rlp(TransactionType || TransactionPayload)
                : txPayloadLength;
            return result;
        }
    }
}
