// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

namespace Nethermind.Serialization.Rlp
{
    public class HeaderDecoder : IRlpValueDecoder<BlockHeader>, IRlpStreamDecoder<BlockHeader>
    {
        public BlockHeader? Decode(ref Rlp.ValueDecoderContext decoderContext,
            RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (decoderContext.IsNextItemNull())
            {
                return null;
            }

            var headerRlp = decoderContext.PeekNextItem();
            int headerSequenceLength = decoderContext.ReadSequenceLength();
            int headerCheck = decoderContext.Position + headerSequenceLength;

            Keccak? parentHash = decoderContext.DecodeKeccak();
            Keccak? unclesHash = decoderContext.DecodeKeccak();
            Address? beneficiary = decoderContext.DecodeAddress();
            Keccak? stateRoot = decoderContext.DecodeKeccak();
            Keccak? transactionsRoot = decoderContext.DecodeKeccak();
            Keccak? receiptsRoot = decoderContext.DecodeKeccak();
            Bloom? bloom = decoderContext.DecodeBloom();
            UInt256 difficulty = decoderContext.DecodeUInt256();
            long number = decoderContext.DecodeLong();
            long gasLimit = decoderContext.DecodeLong();
            long gasUsed = decoderContext.DecodeLong();
            ulong timestamp = decoderContext.DecodeULong();
            byte[]? extraData = decoderContext.DecodeByteArray();

            BlockHeader blockHeader = new(
                parentHash,
                unclesHash,
                beneficiary,
                difficulty,
                number,
                gasLimit,
                timestamp,
                extraData)
            {
                StateRoot = stateRoot,
                TxRoot = transactionsRoot,
                ReceiptsRoot = receiptsRoot,
                Bloom = bloom,
                GasUsed = gasUsed,
                Hash = Keccak.Compute(headerRlp)
            };

            if (decoderContext.PeekPrefixAndContentLength().ContentLength == Keccak.Size)
            {
                blockHeader.MixHash = decoderContext.DecodeKeccak();
                blockHeader.Nonce = (ulong)decoderContext.DecodeUBigInt();
            }
            else
            {
                blockHeader.AuRaStep = (long)decoderContext.DecodeUInt256();
                blockHeader.AuRaSignature = decoderContext.DecodeByteArray();
            }

            // if we didn't reach the end of the stream, assume we have basefee to decode
            if (decoderContext.Position != headerCheck)
            {
                blockHeader.BaseFeePerGas = decoderContext.DecodeUInt256();
            }

            int itemsRemaining = decoderContext.PeekNumberOfItemsRemaining(null, 3);
            if (itemsRemaining > 0 &&
                decoderContext.PeekPrefixAndContentLength().ContentLength == Keccak.Size)
            {
                blockHeader.WithdrawalsRoot = decoderContext.DecodeKeccak();

                if (itemsRemaining >= 3 && decoderContext.Position != headerCheck)
                {
                    blockHeader.BlobGasUsed = decoderContext.DecodeULong(allowLeadingZeroBytes: false);
                    blockHeader.ExcessBlobGas = decoderContext.DecodeULong(allowLeadingZeroBytes: false);
                }

                if (itemsRemaining == 4 && decoderContext.Position != headerCheck)
                {
                    blockHeader.ParentBeaconBlockRoot = decoderContext.DecodeKeccak();
                }
            }


            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                decoderContext.Check(headerCheck);
            }

            return blockHeader;
        }

        public BlockHeader? Decode(RlpStream rlpStream, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (rlpStream.IsNextItemNull())
            {
                rlpStream.ReadByte();
                return null;
            }

            Span<byte> headerRlp = rlpStream.PeekNextItem();
            int headerSequenceLength = rlpStream.ReadSequenceLength();
            int headerCheck = rlpStream.Position + headerSequenceLength;

            Keccak? parentHash = rlpStream.DecodeKeccak();
            Keccak? unclesHash = rlpStream.DecodeKeccak();
            Address? beneficiary = rlpStream.DecodeAddress();
            Keccak? stateRoot = rlpStream.DecodeKeccak();
            Keccak? transactionsRoot = rlpStream.DecodeKeccak();
            Keccak? receiptsRoot = rlpStream.DecodeKeccak();
            Bloom? bloom = rlpStream.DecodeBloom();
            UInt256 difficulty = rlpStream.DecodeUInt256();
            long number = rlpStream.DecodeLong();
            long gasLimit = rlpStream.DecodeLong();
            long gasUsed = rlpStream.DecodeLong();
            ulong timestamp = rlpStream.DecodeULong();
            byte[]? extraData = rlpStream.DecodeByteArray();

            BlockHeader blockHeader = new(
                parentHash,
                unclesHash,
                beneficiary,
                difficulty,
                number,
                gasLimit,
                timestamp,
                extraData)
            {
                StateRoot = stateRoot,
                TxRoot = transactionsRoot,
                ReceiptsRoot = receiptsRoot,
                Bloom = bloom,
                GasUsed = gasUsed,
                Hash = Keccak.Compute(headerRlp)
            };

            if (rlpStream.PeekPrefixAndContentLength().ContentLength == Keccak.Size)
            {
                blockHeader.MixHash = rlpStream.DecodeKeccak();
                blockHeader.Nonce = (ulong)rlpStream.DecodeUBigInt();
            }
            else
            {
                blockHeader.AuRaStep = (long)rlpStream.DecodeUInt256();
                blockHeader.AuRaSignature = rlpStream.DecodeByteArray();
            }

            if (rlpStream.Position != headerCheck)
            {
                blockHeader.BaseFeePerGas = rlpStream.DecodeUInt256();
            }

            int itemsRemaining = rlpStream.PeekNumberOfItemsRemaining(null, 3);
            if (itemsRemaining > 0 &&
                rlpStream.PeekPrefixAndContentLength().ContentLength == Keccak.Size)
            {
                blockHeader.WithdrawalsRoot = rlpStream.DecodeKeccak();

                if (itemsRemaining >= 3 && rlpStream.Position != headerCheck)
                {
                    blockHeader.BlobGasUsed = rlpStream.DecodeUlong(allowLeadingZeroBytes: false);
                    blockHeader.ExcessBlobGas = rlpStream.DecodeUlong(allowLeadingZeroBytes: false);
                }

                if (itemsRemaining == 4 && rlpStream.Position != headerCheck)
                {
                    blockHeader.ParentBeaconBlockRoot = rlpStream.DecodeKeccak();
                }
            }

            if ((rlpBehaviors & RlpBehaviors.AllowExtraBytes) != RlpBehaviors.AllowExtraBytes)
            {
                rlpStream.Check(headerCheck);
            }

            return blockHeader;
        }

        public void Encode(RlpStream rlpStream, BlockHeader? header, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (header is null)
            {
                rlpStream.EncodeNullObject();
                return;
            }

            bool notForSealing = (rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing;
            rlpStream.StartSequence(GetContentLength(header, rlpBehaviors));
            rlpStream.Encode(header.ParentHash);
            rlpStream.Encode(header.UnclesHash);
            rlpStream.Encode(header.Beneficiary);
            rlpStream.Encode(header.StateRoot);
            rlpStream.Encode(header.TxRoot);
            rlpStream.Encode(header.ReceiptsRoot);
            rlpStream.Encode(header.Bloom);
            rlpStream.Encode(header.Difficulty);
            rlpStream.Encode(header.Number);
            rlpStream.Encode(header.GasLimit);
            rlpStream.Encode(header.GasUsed);
            rlpStream.Encode(header.Timestamp);
            rlpStream.Encode(header.ExtraData);

            if (notForSealing)
            {
                bool isAuRa = header.AuRaSignature is not null;
                if (isAuRa)
                {
                    rlpStream.Encode(header.AuRaStep!.Value);
                    rlpStream.Encode(header.AuRaSignature);
                }
                else
                {
                    rlpStream.Encode(header.MixHash);
                    rlpStream.EncodeNonce(header.Nonce);
                }
            }

            if (!header.BaseFeePerGas.IsZero)
            {
                rlpStream.Encode(header.BaseFeePerGas);
            }

            if (header.WithdrawalsRoot is not null || header.ExcessBlobGas is not null || header.BlobGasUsed is not null)
            {
                rlpStream.Encode(header.WithdrawalsRoot ?? Keccak.Zero);
            }

            if (header.BlobGasUsed is not null || header.ExcessBlobGas is not null)
            {
                rlpStream.Encode(header.BlobGasUsed.GetValueOrDefault());
                rlpStream.Encode(header.ExcessBlobGas.GetValueOrDefault());
            }

            if (header.ParentBeaconBlockRoot is not null)
            {
                rlpStream.Encode(header.ParentBeaconBlockRoot ?? Keccak.Zero);
            }
        }

        public Rlp Encode(BlockHeader? item, RlpBehaviors rlpBehaviors = RlpBehaviors.None)
        {
            if (item is null)
            {
                return Rlp.OfEmptySequence;
            }

            RlpStream rlpStream = new(GetLength(item, rlpBehaviors));
            Encode(rlpStream, item, rlpBehaviors);

            return new Rlp(rlpStream.Data);
        }

        private static int GetContentLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
        {
            if (item is null)
            {
                return 0;
            }

            bool notForSealing = (rlpBehaviors & RlpBehaviors.ForSealing) != RlpBehaviors.ForSealing;
            int contentLength = 0
                                + Rlp.LengthOf(item.ParentHash)
                                + Rlp.LengthOf(item.UnclesHash)
                                + Rlp.LengthOf(item.Beneficiary)
                                + Rlp.LengthOf(item.StateRoot)
                                + Rlp.LengthOf(item.TxRoot)
                                + Rlp.LengthOf(item.ReceiptsRoot)
                                + Rlp.LengthOf(item.Bloom)
                                + Rlp.LengthOf(item.Difficulty)
                                + Rlp.LengthOf(item.Number)
                                + Rlp.LengthOf(item.GasLimit)
                                + Rlp.LengthOf(item.GasUsed)
                                + Rlp.LengthOf(item.Timestamp)
                                + Rlp.LengthOf(item.ExtraData)
                                + (item.BaseFeePerGas.IsZero ? 0 : Rlp.LengthOf(item.BaseFeePerGas))
                                + (item.WithdrawalsRoot is null && item.BlobGasUsed is null && item.ExcessBlobGas is null ? 0 : Rlp.LengthOfKeccakRlp)
                                + (item.ParentBeaconBlockRoot is null ? 0 : Rlp.LengthOfKeccakRlp)
                                + (item.BlobGasUsed is null ? 0 : Rlp.LengthOf(item.BlobGasUsed.Value))
                                + (item.ExcessBlobGas is null ? 0 : Rlp.LengthOf(item.ExcessBlobGas.Value));

            if (notForSealing)
            {
                bool isAuRa = item.AuRaSignature is not null;
                if (isAuRa)
                {
                    contentLength += Rlp.LengthOf(item.AuRaStep!.Value);
                    contentLength += Rlp.LengthOf(item.AuRaSignature);
                }
                else
                {
                    contentLength += Rlp.LengthOf(item.MixHash);
                    contentLength += Rlp.LengthOfNonce(item.Nonce);
                }
            }

            return contentLength;
        }

        public int GetLength(BlockHeader? item, RlpBehaviors rlpBehaviors)
        {
            return Rlp.LengthOfSequence(GetContentLength(item, rlpBehaviors));
        }
    }
}
