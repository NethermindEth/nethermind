// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.BalRecorder;

public class RecordedBalStore(string directory, bool replayEnabled, bool recordingEnabled) : IRecordedBalStore
{
    public bool ReplayEnabled => replayEnabled;
    public bool RecordingEnabled => recordingEnabled;

    private readonly EraFlatStore _store = new(directory, "bal");

    public void Insert(Block block, BlockAccessList bal)
    {
        using NettyRlpStream rlp = BlockAccessListDecoder.Instance.EncodeToNewNettyStream(bal);
        _store.Write(block.Number, rlp.AsSpan());
    }

    public BlockAccessList? Get(long blockNumber, Hash256 blockHash)
    {
        BlockAccessList? result = null;
        _store.TryRead(blockNumber, (data, _) => result = BlockAccessListDecoder.Instance.Decode(data), 0);
        return result;
    }
}

public class NullRecordedBalStore : IRecordedBalStore
{
    public static NullRecordedBalStore Instance { get; } = new();
    public void Insert(Block block, BlockAccessList bal) { }
    public BlockAccessList? Get(long blockNumber, Hash256 blockHash) => null;
    public bool ReplayEnabled => false;
    public bool RecordingEnabled => false;
}
