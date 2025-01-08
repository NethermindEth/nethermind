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

    public byte[]? EncodeHeader(BlockHeader header)
    {
        var headerBytes = _headerDecoder.Encode(header).Bytes;

        // TODO: Proof
        return SszEncoding.Encode(new PortalBlockHeaderWithProof()
        {
            Header = headerBytes,
            Proof = new PortalBlockHeaderProof { Selector = PortalBlockHeaderProofSelector.None },
        });
    }

    public BlockBody DecodeBody(byte[] payload)
    {
        try
        {
            SszEncoding.Decode(payload, out PortalBlockBodyPostShanghai body);
            Transaction[] transactions = body.Transactions
              .Select((tx) => _txDecoder.Decode(new RlpStream(tx.Data))!).ToArray();
            BlockHeader[] uncles = new HeaderDecoder().DecodeArray(new RlpStream(body.Uncles));

            Withdrawal[] withdrawals = body.Withdrawals
              .Select((w) => new WithdrawalDecoder().Decode(new RlpStream(w.Data))!).ToArray();

            return new BlockBody(transactions, uncles, withdrawals);
        }
        catch
        {
            SszEncoding.Decode(payload, out PortalBlockBodyPreShanghai body);
            Transaction[] transactions = body.Transactions
                         .Select((tx) => _txDecoder.Decode(new RlpStream(tx.Data))!).ToArray();
            BlockHeader[] uncles = new HeaderDecoder().DecodeArray(new RlpStream(body.Uncles));

            return new BlockBody(transactions, uncles);
        }
    }

    public byte[]? EncodeBlockBody(BlockBody blockBody)
    {
        SszTransaction[] transactionBytes = blockBody.Transactions
            .Select((tx) => new SszTransaction() { Data = _txDecoder.Encode(tx).Bytes }).ToArray();
        byte[] unclesBytes = Rlp.Encode(blockBody.Uncles).Bytes;
        EncodedWidthrawals[]? withdrawalBytes = blockBody.Withdrawals?
            .Select((w) => new EncodedWidthrawals() { Data = new WithdrawalDecoder().Encode(w).Bytes }).ToArray();

        // TODO: Uncles widthrawals
        return withdrawalBytes is not null ? SszEncoding.Encode(new PortalBlockBodyPostShanghai()
        {
            Transactions = transactionBytes,
            Uncles = unclesBytes,
            Withdrawals = withdrawalBytes,
        }) : SszEncoding.Encode(new PortalBlockBodyPreShanghai()
        {
            Transactions = transactionBytes,
            Uncles = unclesBytes,
        });
    }

    public TxReceipt[] DecodeReceipt(byte[] payload)
    {
        SszEncoding.Decode(payload, out SszReceipt[] receipts);
        TxReceipt[] txReceipts = receipts
            .Select((r) => new ReceiptStorageDecoder().Decode(new RlpStream(r.Data))!).ToArray();

        return txReceipts;
    }

    public byte[]? EncodeReceipts(TxReceipt[] receipts)
    {
        SszReceipt[] sszReceipts = receipts
            .Select((r) =>
            {
                RlpStream str = new(_receiptDecoder.GetLength(r, RlpBehaviors.None));
                _receiptDecoder.Encode(str, r, RlpBehaviors.None);
                return new SszReceipt() { Data = str.Data.ToArray()! };
            }).ToArray();

        return SszEncoding.Encode(sszReceipts);
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
