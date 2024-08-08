// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System.Runtime.CompilerServices;
using System.Text;
using Nethermind.Core.Crypto;
using Nethermind.Int256;

[assembly: InternalsVisibleTo("Nethermind.Consensus")]
namespace Nethermind.Core.Optimism;

public class DepositTransaction : Transaction
{
    public const byte Code = 0x7E;

    // SourceHash uniquely identifies the source of the deposit
    public Hash256? SourceHash { get; set; }

    // Mint is minted on L2, locked on L1, nil if no minting.
    public UInt256 Mint { get; set; }

    // Field indicating if this transaction is exempt from the L2 gas limit.
    public bool IsOPSystemTransaction { get; set; }

    public override string ToString(string indent)
    {
        StringBuilder builder = new(base.ToString(indent));

        builder.AppendLine($"{indent}SourceHash: {SourceHash}");
        builder.AppendLine($"{indent}Mint:      {Mint}");
        builder.AppendLine($"{indent}OpSystem:  {IsOPSystemTransaction}");

        return builder.ToString();
    }

    public override string ToString() => ToString(string.Empty);

    public void CopyTo(DepositTransaction tx)
    {
        tx.ChainId = ChainId;
        tx.Type = Type;
        tx.SourceHash = SourceHash;
        tx.Mint = Mint;
        tx.IsOPSystemTransaction = IsOPSystemTransaction;
        tx.Nonce = Nonce;
        tx.GasPrice = GasPrice;
        tx.GasBottleneck = GasBottleneck;
        tx.DecodedMaxFeePerGas = DecodedMaxFeePerGas;
        tx.GasLimit = GasLimit;
        tx.To = To;
        tx.Value = Value;
        tx.Data = Data;
        tx.SenderAddress = SenderAddress;
        tx.Signature = Signature;
        tx.Timestamp = Timestamp;
        tx.AccessList = AccessList;
        tx.MaxFeePerBlobGas = MaxFeePerBlobGas;
        tx.BlobVersionedHashes = BlobVersionedHashes;
        tx.NetworkWrapper = NetworkWrapper;
        tx.IsServiceTransaction = IsServiceTransaction;
        tx.PoolIndex = PoolIndex;
        tx._size = _size;
    }
}
