// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.BlockAccessLists;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;
using Nethermind.Serialization.Rlp.Eip7928;

namespace Nethermind.BalRecorder;

public class RecordedBalStore(string directory, IBalRecorderConfig config) : IRecordedBalStore
{
    private readonly SlotStore _store = new(directory, "bal");

    public bool ReplayEnabled => config.ReplayEnabled;
    public bool RecordingEnabled => config.RecordingEnabled;

    public void Dispose() => _store.Dispose();

    public void Insert(Block block, BlockAccessList bal)
    {
        using NettyRlpStream rlp = BlockAccessListDecoder.Instance.EncodeToNewNettyStream(bal);
        _store.Write(block.Number, rlp.AsSpan());
    }

    public BlockAccessList? Get(long blockNumber, Hash256 blockHash)
    {
        ResultRef resultRef = new();
        _store.TryRead(blockNumber, static (data, state) => state.Value = BlockAccessListDecoder.Instance.Decode(data), resultRef);
        return resultRef.Value;
    }

    private sealed class ResultRef { public BlockAccessList? Value; }
}

public class NullRecordedBalStore : IRecordedBalStore
{
    public static NullRecordedBalStore Instance { get; } = new();
    public void Insert(Block block, BlockAccessList bal) { }
    public BlockAccessList? Get(long blockNumber, Hash256 blockHash) => null;
    public bool ReplayEnabled => false;
    public bool RecordingEnabled => false;
    public void Dispose() { }
}
