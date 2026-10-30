// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Facade.Eth;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Eez.Follower;

internal sealed class SequencerRpcBlock : BlockHeaderForRpc
{
    public Hash256[]? Transactions { get; set; }
    public Hash256[]? Uncles { get; set; }
    public Withdrawal[]? Withdrawals { get; set; }

    internal BlockHeader ToHeader(Hash256 expected)
    {
        if (Hash != expected || Number is null || Nonce is null || ParentHash is null || Sha3Uncles is null ||
            Miner is null || StateRoot is null || TransactionsRoot is null || ReceiptsRoot is null || LogsBloom is null ||
            MixHash is null || BaseFeePerGas is null || Transactions is null || Uncles is null || Uncles.Length != 0 ||
            Sha3Uncles != Keccak.OfAnEmptySequenceRlp || (WithdrawalsRoot is null) != (Withdrawals is null) || Timestamp > ulong.MaxValue)
            throw new L1SourceIncompleteException(Number ?? 0, "The sequencer returned an incomplete or unsupported block.");
        BlockHeader header = new(ParentHash, Sha3Uncles, Miner, Difficulty, Number.Value, GasLimit, (ulong)Timestamp, ExtraData)
        {
            Hash = expected,
            Nonce = Nonce.Value,
            MixHash = MixHash,
            StateRoot = StateRoot,
            TxRoot = TransactionsRoot,
            ReceiptsRoot = ReceiptsRoot,
            Bloom = LogsBloom,
            GasUsed = GasUsed,
            BaseFeePerGas = BaseFeePerGas.Value,
            WithdrawalsRoot = WithdrawalsRoot,
            BlobGasUsed = BlobGasUsed,
            ExcessBlobGas = ExcessBlobGas,
            ParentBeaconBlockRoot = ParentBeaconBlockRoot,
            RequestsHash = RequestsHash,
            BlockAccessListHash = BlockAccessListHash,
            SlotNumber = SlotNumber,
        };
        if (Keccak.Compute(Rlp.Encode(header).Bytes) != expected)
            throw new L1SourceIncompleteException(header.Number, "The sequencer header fields do not match its hash.");
        return header;
    }
}
