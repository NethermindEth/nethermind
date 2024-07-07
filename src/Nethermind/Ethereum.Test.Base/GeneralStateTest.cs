// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.IO;
using Ethereum.Test.Base.Interfaces;
using Nethermind.Consensus.Ethash;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Specs;
using Nethermind.Core.Test.Builders;
using Nethermind.Evm;
using Nethermind.Int256;
using Nethermind.Specs.Forks;

namespace Ethereum.Test.Base
{
    public class GeneralStateTest : IEthereumTest
    {
        public string? Category { get; set; }
        public string? Name { get; set; }
        public IReleaseSpec? Fork { get; set; }
        public string? ForkName { get; set; }
        public Address? CurrentCoinbase { get; set; }
        public UInt256? CurrentDifficulty { get; set; }

        public UInt256? CurrentBaseFee { get; set; }
        public long CurrentGasLimit { get; set; }
        public long CurrentNumber { get; set; }
        public ulong CurrentTimestamp { get; set; }
        public Hash256? PreviousHash { get; set; }
        public Dictionary<Address, AccountState> Pre { get; set; }
        public Hash256? PostHash { get; set; }
        public Hash256? PostReceiptsRoot { get; set; }
        public string? LoadFailure { get; set; }
        public Transaction[] Transactions { get; set; } = [];
        public Hash256? CurrentRandom { get; set; }
        public Hash256? CurrentBeaconRoot { get; set; }
        public Hash256? CurrentWithdrawalsRoot { get; set; }
        public ulong? CurrentExcessBlobGas { get; set; }
        public UInt256? ParentBlobGasUsed { get; set; }
        public UInt256? ParentExcessBlobGas { get; set; }

        public Withdrawal[]? Withdrawals { get; set; }
        public ulong ParentTimestamp { get; set; } // optional?
        public UInt256? ParentDifficulty { get; set; }
        public Hash256? ParentUncleHash { get; set; }
        public Hash256? ParentBeaconBlockRoot { get; set; }
        public UInt256? ParentBaseFee { get; set; }
        public long ParentGasUsed { get; set; }
        public long ParentGasLimit { get; set; }
        public Dictionary<string, Hash256> BlockHashes { get; set; } = [];
        public Ommer[] Ommers { get; set; } = [];

        private static readonly UInt256 _defaultBaseFeeForStateTest = 0xA;

        public override string ToString()
        {
            return $"{Path.GetFileName(Category)}.{Name}_{ForkName}";
        }

        public BlockHeader GetBlockHeader(BlockHeader parentBlockHeader)
        {
            BlockHeaderBuilder blockHeaderBuilder = new();

            if (CurrentDifficulty.HasValue) blockHeaderBuilder.WithDifficulty(CurrentDifficulty.Value);
            blockHeaderBuilder.WithNumber(CurrentNumber);
            blockHeaderBuilder.WithGasLimit(CurrentGasLimit);
            blockHeaderBuilder.WithBeneficiary(CurrentCoinbase ?? throw new Exception("CurrentCoinbase is missing"));
            blockHeaderBuilder.WithExcessBlobGas(CurrentExcessBlobGas ?? BlobGasCalculator.CalculateExcessBlobGas(parentBlockHeader, Fork));
            blockHeaderBuilder.WithParentBeaconBlockRoot(ParentBeaconBlockRoot);
            if (CurrentBaseFee.HasValue) blockHeaderBuilder.WithBaseFee(CurrentBaseFee.Value);
            blockHeaderBuilder.WithBaseFee(Fork.IsEip1559Enabled ? CurrentBaseFee ?? _defaultBaseFeeForStateTest : UInt256.Zero);

            blockHeaderBuilder.WithTimestamp(CurrentTimestamp);
            if (CurrentRandom is not null) blockHeaderBuilder.WithMixHash(new Hash256(CurrentRandom));

            return blockHeaderBuilder.TestObject;
        }

        public BlockHeader GetParentBlockHeader()
        {
            BlockHeaderBuilder blockHeaderBuilder = new();
            if (ParentDifficulty.HasValue) blockHeaderBuilder.WithDifficulty(ParentDifficulty.Value);
            blockHeaderBuilder.WithNumber(CurrentNumber - 1);
            blockHeaderBuilder.WithGasLimit(ParentGasLimit);
            if (ParentExcessBlobGas is not null) blockHeaderBuilder.WithExcessBlobGas((ulong) ParentExcessBlobGas);
            if (ParentBaseFee.HasValue) blockHeaderBuilder.WithBaseFee(ParentBaseFee.Value);
            if (ParentBlobGasUsed is not null) blockHeaderBuilder.WithBlobGasUsed((ulong) ParentBlobGasUsed);
            blockHeaderBuilder.WithGasUsed(ParentGasUsed);
            blockHeaderBuilder.WithTimestamp(ParentTimestamp);

            if (ParentUncleHash != null) blockHeaderBuilder.WithUnclesHash(ParentUncleHash);
            return blockHeaderBuilder.TestObject;
        }

        public void ApplyChecks(ISpecProvider specProvider, BlockHeader parentBlockHeader)
        {
            ApplyLondonChecks(parentBlockHeader);
            ApplyShanghaiChecks();
            ApplyCancunChecks();
            ApplyMergeChecks(specProvider);
        }

        private void ApplyLondonChecks(BlockHeader parentBlockHeader)
        {
            if (!Fork.IsEip1559Enabled) return;
            if (CurrentBaseFee != null) return;

            if (!ParentBaseFee.HasValue || CurrentNumber == 0)
            {
                throw new T8NException("EIP-1559 config but missing 'currentBaseFee' in env section", T8NToolExitCodes.ErrorConfig);
            }
            CurrentBaseFee = BaseFeeCalculator.Calculate(parentBlockHeader, Fork);
        }

        private void ApplyShanghaiChecks()
        {
            if (Fork is not Shanghai) return;
            if (Withdrawals == null)
            {
                throw new T8NException("Shanghai config but missing 'withdrawals' in env section", T8NToolExitCodes.ErrorConfig);
            }
        }

        private void ApplyCancunChecks()
        {
            if (Fork is not Cancun)
            {
                ParentBeaconBlockRoot = null;
                return;
            }

            if (ParentBeaconBlockRoot == null)
            {
                throw new T8NException("post-cancun env requires parentBeaconBlockRoot to be set", T8NToolExitCodes.ErrorConfig);
            }
        }

        private void ApplyMergeChecks(ISpecProvider specProvider)
        {
            if (specProvider.TerminalTotalDifficulty?.IsZero ?? false)
            {
                if (CurrentRandom == null) throw new T8NException("post-merge requires currentRandom to be defined in env", T8NToolExitCodes.ErrorConfig);
                if (CurrentDifficulty?.IsZero ?? false) throw new T8NException("post-merge difficulty must be zero (or omitted) in env", T8NToolExitCodes.ErrorConfig);
                return;
            }
            if (CurrentDifficulty != null) return;
            if (!ParentDifficulty.HasValue)
            {
                throw new T8NException(
                    "currentDifficulty was not provided, and cannot be calculated due to missing parentDifficulty", T8NToolExitCodes.ErrorConfig);
            }

            if (CurrentNumber == 0)
            {
                throw new T8NException("currentDifficulty needs to be provided for block number 0", T8NToolExitCodes.ErrorConfig);
            }

            if (CurrentTimestamp <= ParentTimestamp)
            {
                throw new T8NException($"currentDifficulty cannot be calculated -- currentTime ({CurrentTimestamp}) needs to be after parent time ({ParentTimestamp})", T8NToolExitCodes.ErrorConfig);
            }

            EthashDifficultyCalculator difficultyCalculator = new(specProvider);

            CurrentDifficulty = difficultyCalculator.Calculate(ParentDifficulty.Value, ParentTimestamp, CurrentTimestamp, CurrentNumber, ParentUncleHash is not null);
        }
    }
}
