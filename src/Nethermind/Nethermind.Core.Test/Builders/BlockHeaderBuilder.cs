// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using Nethermind.Core.Crypto;
using Nethermind.Crypto;
using Nethermind.Int256;

namespace Nethermind.Core.Test.Builders;

public class BlockHeaderBuilder : BuilderBase<BlockHeader>
{
    public static UInt256 DefaultDifficulty = 1_000_000;

    protected override void BeforeReturn()
    {
        if (!_doNotCalculateHash)
        {
            TestObjectInternal.Hash = TestObjectInternal.CalculateHash();
        }

        base.BeforeReturn();
    }

    public BlockHeaderBuilder()
    {
        TestObjectInternal = new BlockHeader(
            new Keccak(Keccak.Compute("parent")),
            Keccak.OfAnEmptySequenceRlp,
            Address.Zero,
            DefaultDifficulty, 0,
            4_000_000,
            1_000_000,
            new byte[] { 1, 2, 3 },
            null);
        TestObjectInternal.Bloom = Bloom.Empty;
        TestObjectInternal.MixHash = new Keccak(Keccak.Compute("mix_hash"));
        TestObjectInternal.Nonce = 1000;
        TestObjectInternal.ReceiptsRoot = Keccak.EmptyTreeHash;
        TestObjectInternal.StateRoot = Keccak.EmptyTreeHash;
        TestObjectInternal.TxRoot = Keccak.EmptyTreeHash;
    }

    public BlockHeaderBuilder WithParent(BlockHeader parentHeader)
    {
        TestObjectInternal.ParentHash = parentHeader.Hash;
        TestObjectInternal.Number = parentHeader.Number + 1;
        TestObjectInternal.GasLimit = parentHeader.GasLimit;
        return this;
    }

    public BlockHeaderBuilder WithParentHash(ValueKeccak parentHash)
    {
        TestObjectInternal.ParentHash = new Keccak(parentHash);
        return this;
    }

    public BlockHeaderBuilder WithParentHash(Keccak parentHash)
    {
        TestObjectInternal.ParentHash = parentHash;
        return this;
    }

    public BlockHeaderBuilder WithHash(ValueKeccak hash)
    {
        TestObjectInternal.Hash = new Keccak(hash);
        _doNotCalculateHash = true;
        return this;
    }

    public BlockHeaderBuilder WithHash(Keccak hash)
    {
        TestObjectInternal.Hash = hash;
        _doNotCalculateHash = true;
        return this;
    }

    private bool _doNotCalculateHash;

    public BlockHeaderBuilder WithUnclesHash(Keccak unclesHash)
    {
        TestObjectInternal.UnclesHash = unclesHash;
        return this;
    }

    public BlockHeaderBuilder WithBeneficiary(Address beneficiary)
    {
        TestObjectInternal.Beneficiary = beneficiary;
        return this;
    }

    public BlockHeaderBuilder WithAuthor(Address address)
    {
        TestObjectInternal.Author = address;
        return this;
    }

    public BlockHeaderBuilder WithBloom(Bloom bloom)
    {
        TestObjectInternal.Bloom = bloom;
        return this;
    }

    public BlockHeaderBuilder WithBaseFee(UInt256 baseFee)
    {
        TestObjectInternal.BaseFeePerGas = baseFee;
        return this;
    }

    public BlockHeaderBuilder WithStateRoot(ValueKeccak stateRoot)
    {
        TestObjectInternal.StateRoot = new Keccak(stateRoot);
        return this;
    }

    public BlockHeaderBuilder WithStateRoot(Keccak stateRoot)
    {
        TestObjectInternal.StateRoot = stateRoot;
        return this;
    }

    public BlockHeaderBuilder WithTransactionsRoot(ValueKeccak transactionsRoot)
    {
        TestObjectInternal.TxRoot = new Keccak(transactionsRoot);
        return this;
    }

    public BlockHeaderBuilder WithTransactionsRoot(Keccak transactionsRoot)
    {
        TestObjectInternal.TxRoot = transactionsRoot;
        return this;
    }

    public BlockHeaderBuilder WithReceiptsRoot(ValueKeccak receiptsRoot)
    {
        TestObjectInternal.ReceiptsRoot = new Keccak(receiptsRoot);
        return this;
    }

    public BlockHeaderBuilder WithReceiptsRoot(Keccak receiptsRoot)
    {
        TestObjectInternal.ReceiptsRoot = receiptsRoot;
        return this;
    }

    public BlockHeaderBuilder WithDifficulty(UInt256 difficulty)
    {
        TestObjectInternal.Difficulty = difficulty;
        return this;
    }

    public BlockHeaderBuilder WithNumber(long blockNumber)
    {
        TestObjectInternal.Number = blockNumber;
        return this;
    }

    public BlockHeaderBuilder WithTotalDifficulty(long totalDifficulty)
    {
        TestObjectInternal.TotalDifficulty = (ulong)totalDifficulty;
        return this;
    }

    public BlockHeaderBuilder WithGasLimit(long gasLimit)
    {
        TestObjectInternal.GasLimit = gasLimit;
        return this;
    }

    public BlockHeaderBuilder WithGasUsed(long gasUsed)
    {
        TestObjectInternal.GasUsed = gasUsed;
        return this;
    }

    public BlockHeaderBuilder WithTimestamp(ulong timestamp)
    {
        TestObjectInternal.Timestamp = timestamp;
        return this;
    }

    public BlockHeaderBuilder WithExtraData(byte[] extraData)
    {
        TestObjectInternal.ExtraData = extraData;
        return this;
    }

    public BlockHeaderBuilder WithMixHash(ValueKeccak mixHash)
    {
        TestObjectInternal.MixHash = new Keccak(mixHash);
        return this;
    }

    public BlockHeaderBuilder WithMixHash(Keccak mixHash)
    {
        TestObjectInternal.MixHash = mixHash;
        return this;
    }

    public BlockHeaderBuilder WithNonce(ulong nonce)
    {
        TestObjectInternal.Nonce = nonce;
        return this;
    }

    public BlockHeaderBuilder WithAura(long step, byte[]? signature = null)
    {
        TestObjectInternal.AuRaStep = step;
        TestObjectInternal.AuRaSignature = signature;
        return this;
    }

    public BlockHeaderBuilder WithWithdrawalsRoot(ValueKeccak root)
    {
        TestObjectInternal.WithdrawalsRoot = new Keccak(root);

        return this;
    }

    public BlockHeaderBuilder WithWithdrawalsRoot(Keccak? root)
    {
        TestObjectInternal.WithdrawalsRoot = root;

        return this;
    }

    public BlockHeaderBuilder WithExcessDataGas(UInt256? excessDataGas)
    {
        TestObjectInternal.ExcessDataGas = excessDataGas;
        return this;
    }
}
