/*
 * Copyright (c) 2018 Demerzel Solutions Limited
 * This file is part of the Nethermind library.
 *
 * The Nethermind library is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Lesser General Public License as published by
 * the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * The Nethermind library is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Lesser General Public License for more details.
 *
 * You should have received a copy of the GNU Lesser General Public License
 * along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.
 */

using Nethermind.Core.Crypto;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Core.Test.Builders
{
    public class ReceiptBuilder : BuilderBase<TxReceipt>
    {
        public ReceiptBuilder()
        {
            TestObjectInternal = new TxReceipt();
            TestObjectInternal.Logs = new[] {new LogEntry(Address.Zero, new byte[0], new[] {Keccak.Zero}),};
        }

        public ReceiptBuilder WithAllFieldsFilled => WithBloom(Test.Builders.TestItem.NonZeroBloom)
            .WithError("error")
            .WithIndex(2)
            .WithSender(Test.Builders.TestItem.AddressA)
            .WithRecipient(Test.Builders.TestItem.AddressB)
            .WithContractAddress(Test.Builders.TestItem.AddressC)
            .WithGasUsed(100)
            .WithTransactionHash(Builders.TestItem.KeccakA)
            .WithState(Builders.TestItem.KeccakB)
            .WithBlockHash(Builders.TestItem.KeccakC)
            .WithBlockNumber(2)
            .WithBloom(Bloom.Empty)
            .WithGasUsedTotal(1000)
            .WithStatusCode(1);

        public ReceiptBuilder WithState(Keccak state)
        {
            TestObjectInternal.PostTransactionState = state;
            return this;
        }

        public ReceiptBuilder WithLogs(LogEntry[] logs)
        {
            TestObjectInternal.Logs = logs;
            return this;
        }

        public ReceiptBuilder WithTransactionHash(Keccak hash)
        {
            TestObject.TxHash = hash;
            return this;
        }

        public ReceiptBuilder WithBlockNumber(long number)
        {
            TestObject.BlockNumber = number;
            return this;
        }

        public ReceiptBuilder WithBlockHash(Keccak hash)
        {
            TestObject.BlockHash = hash;
            return this;
        }
        
        public ReceiptBuilder WithGasUsedTotal(long gasTotal)
        {
            TestObjectInternal.GasUsedTotal = gasTotal;
            return this;
        }

        public ReceiptBuilder WithGasUsed(long gasUsed)
        {
            TestObjectInternal.GasUsed = gasUsed;
            return this;
        }
        
        public ReceiptBuilder WithBloom(Bloom bloom)
        {
            TestObjectInternal.Bloom = bloom;
            return this;
        }
        
        public ReceiptBuilder WithError(string error)
        {
            TestObjectInternal.Error = error;
            return this;
        }
        
        public ReceiptBuilder WithIndex(int index)
        {
            TestObjectInternal.Index = index;
            return this;
        }
        
        public ReceiptBuilder WithSender(Address sender)
        {
            TestObjectInternal.Sender = sender;
            return this;
        }
        
        public ReceiptBuilder WithContractAddress(Address contractAddress)
        {
            TestObjectInternal.ContractAddress = contractAddress;
            return this;
        }
        
        public ReceiptBuilder WithRecipient(Address recipient)
        {
            TestObjectInternal.Recipient = recipient;
            return this;
        }
        
        public ReceiptBuilder WithStatusCode(byte statusCode)
        {
            TestObjectInternal.StatusCode = statusCode;
            return this;
        }
    }
}