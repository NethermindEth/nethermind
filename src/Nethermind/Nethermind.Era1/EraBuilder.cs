// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Int256;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Era1;
internal class EraBuilder
{
    private long _startNumber;
    private long _startTd;
    private long _totalWritten;
    private List<EntryIndexInfo> _entryIndexInfos = new List<EntryIndexInfo>();

    private BlockDecoder _blockDecoder = new BlockDecoder();
    private ReceiptDecoder _receiptDecoder = new ReceiptDecoder();


    internal EraBuilder()
    {

    }

    public Task Add(Block block, TxReceipt[] receipts)
    {
        Rlp encodedBlock = _blockDecoder.Encode(block);
        throw new NotImplementedException();
    }

    private struct EntryIndexInfo
    {
        public int Index;
        public Keccak Hash;
        public UInt256 TotalDifficulty;
    }
}
