// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;

namespace Nethermind.JsonRpc.Data
{
    public class LogEntryForRpc
    {
        public LogEntryForRpc()
        {
        }

        public LogEntryForRpc(TxReceipt receipt, LogEntry logEntry, int index)
        {
            Removed = false;
            LogIndex = index;
            TransactionIndex = receipt.Index;
            TransactionHash = receipt.TxHash;
            BlockHash = receipt.BlockHash;
            BlockNumber = receipt.BlockNumber;
            Address = logEntry.LoggersAddress;
            Data = logEntry.Data;
            Topics = logEntry.Topics;
        }

        public bool? Removed { get; set; }
        public long? LogIndex { get; set; }
        public long? TransactionIndex { get; set; }
        public Hash256 TransactionHash { get; set; }
        public Hash256 BlockHash { get; set; }
        public long? BlockNumber { get; set; }
        public Address Address { get; set; }
        public byte[] Data { get; set; }
        public Hash256[] Topics { get; set; }

        public LogEntry ToLogEntry()
        {
            return new(Address, Data, Topics);
        }
    }
}
