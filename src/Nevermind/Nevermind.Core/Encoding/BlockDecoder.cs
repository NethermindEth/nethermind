using System;
using System.Collections;
using System.Collections.Generic;

namespace Nevermind.Core.Encoding
{
    public class BlockDecoder : IRlpDecoder<Block>
    {
        private readonly BlockHeaderDecoder _blockHeaderDecoder = new BlockHeaderDecoder();
        private readonly TransactionDecoder _transactionDecoder = new TransactionDecoder();
        
        public Block Decode(Rlp rlp)
        {
            object[] data = (object[])Rlp.Decode(rlp);
            object[] headerData = (object[])data[0];
            object[] transactionsData = (object[])data[1];
            object[] ommersData = (object[])data[2];

            BlockHeader blockHeader = _blockHeaderDecoder.Decode(headerData); 
            
            List<Transaction> transactions = new List<Transaction>();
            foreach (object transactionData in transactionsData)
            {
                transactions.Add(_transactionDecoder.Decode((object[])transactionData));
            }
            
            BlockHeader[] ommers = new BlockHeader[ommersData.Length];
            for (int i = 0; i < ommersData.Length; i++)
            {
                ommers[i] = _blockHeaderDecoder.Decode((object[])ommersData[i]);   
            }
            
            Block block = new Block(blockHeader, ommers);
            block.Transactions = transactions;
            return block;
        }
    }
}