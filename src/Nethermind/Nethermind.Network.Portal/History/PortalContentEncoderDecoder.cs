// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Serialization;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Network.Portal.History;

// Does encode/decode of payload along with verification
// TODO: Do verification here also.
public class HistoryNetworkEncoderDecoder
{
    private HeaderDecoder _headerDecoder = new HeaderDecoder();
    private TxDecoder<Transaction> _txDecoder = TxDecoder.Instance;
    private IRlpStreamDecoder<TxReceipt> _receiptDecoder = new ReceiptStorageDecoder();

    public BlockHeader DecodeHeader(byte[] payload)
    {
        SszEncoding.Decode(payload, out PortalBlockHeaderWithProof headerWithProof);
        var headerBytes = headerWithProof.Header!;
        BlockHeader header = _headerDecoder.Decode(new RlpStream(headerBytes))!;
        return header;
    }

    public BlockBody DecodeBody(byte[] payload)
    {
        // TODO: Need to know if post or pre shanghai.
        // And for that need to get the header first.
        SszEncoding.Decode(payload, out PortalBlockBodyPostShanghai body);
        Transaction[] transactions = body.Transactions
            .Select((tx) => _txDecoder.Decode(new RlpStream(tx.Data))!).ToArray();
        // Does not work. Dont know why.
        // BlockHeader[] uncles = Rlp.Decode<BlockHeader[]>(body.Uncles!);

        // TODO: Widthrawals
        return new BlockBody(transactions, Array.Empty<BlockHeader>());
    }

    public TxReceipt[] DecodeReceipt(byte[] payload)
    {
        // TODO: Need to know if post or pre shanghai.
        // And for that need to get the header first.
        SszEncoding.Decode(payload, out SszReceipt[] receipts);
        TxReceipt[] txReceipts = receipts
            .Select((r) => new ReceiptStorageDecoder().Decode(new RlpStream(r.Data))!).ToArray();
        // Does not work. Dont know why.
        // BlockHeader[] uncles = Rlp.Decode<BlockHeader[]>(body.Uncles!);

        // TODO: Widthrawals
        return txReceipts;// new BlockBody(transactions, Array.Empty<BlockHeader>());
    }

    public byte[]? EncodeHeader(BlockHeader header)
    {
        var headerBytes = _headerDecoder.Encode(header).Bytes;

        // TODO: Proof
        return SszEncoding.Encode(new PortalBlockHeaderWithProof()
        {
            Header = headerBytes
        });
    }

    public byte[]? EncodeBlockBody(BlockBody blockBody)
    {
        SszTransaction[] transactionBytes = blockBody.Transactions
            .Select((tx) => new SszTransaction() { Data = _txDecoder.Encode(tx).Bytes }).ToArray();

        // TODO: Uncles widthrawals
        return SszEncoding.Encode(new PortalBlockBodyPostShanghai()
        {
            Transactions = transactionBytes
        });
    }

    public byte[]? Encode(TxReceipt[] receipts)
    {
        SszReceipt[] receiptBytes = receipts
            .Select((r) =>
            {
                RlpStream str = new(_receiptDecoder.GetLength(r, RlpBehaviors.None));
                _receiptDecoder.Encode(str, r, RlpBehaviors.None);
                return new SszReceipt() { Data = str.Data.ToArray()! };
            }).ToArray();

        byte[] bytes = new byte[SszEncoding.GetLength(receiptBytes)];
        SszEncoding.Encode(bytes.AsSpan(), receiptBytes);

        return bytes;
    }

    /*
    public ContentContent Decode(ContentKey contentKey, byte[] payload)
    {
        ContentContent decodedContent = new ContentContent();
        if (contentKey.HeaderKey != null)
        {
            byte[] headerBytes = SlowSSZ.Deserialize<PortalBlockHeaderWithProof>(payload).Header!;
            BlockHeader header = _headerDecoder.Decode(new RlpStream(headerBytes))!;
            decodedContent.Header = header;
        }
        else if (contentKey.BodyKey != null)
        {
            // TODO: Need to know if post or pre shanghai.
            // And for that need to get the header first.
            PortalBlockBodyPostShanghai body = SlowSSZ.Deserialize<PortalBlockBodyPostShanghai>(payload);
            byte[][] transactionBytes = body.Transactions;
            Transaction[] transactions = transactionBytes.Select((bytes) => _txDecoder.Decode(new RlpStream(bytes))!).ToArray();
            BlockHeader[] uncles = Rlp.Decode<BlockHeader[]>(body.Uncles!);

            // TODO: Widthrawals
            decodedContent.Body = new BlockBody(transactions, uncles);
        }
        else if (contentKey.ReceiptKey != null)
        {
            throw new NotImplementedException("receipt decoding not implemented");
        }
        else
        {
            throw new InvalidOperationException("Unknown decoding");
        }

        return decodedContent;
    }

    public byte[] Encode(ContentContent value)
    {
        throw new NotImplementedException();
    }
    */
}
