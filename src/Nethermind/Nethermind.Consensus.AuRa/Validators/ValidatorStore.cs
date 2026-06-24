// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Autofac.Features.AttributeFilters;
using Nethermind.Core;
using Nethermind.Core.Collections;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class ValidatorStore : IValidatorStore
    {
        internal static readonly Hash256 LatestFinalizedValidatorsBlockNumberKey = Keccak.Compute("LatestFinalizedValidatorsBlockNumber");
        internal static readonly Hash256 PendingValidatorsKey = Keccak.Compute("PendingValidators");
        private static readonly PendingValidatorsDecoder PendingValidatorsDecoder = new();

        private readonly IDb _db;
        private long _latestFinalizedValidatorsBlockNumber;
        private ValidatorInfo? _latestValidatorInfo;
        private static readonly int EmptyBlockNumber = -1;
        private static readonly ValidatorInfo EmptyValidatorInfo = new(-1, -1, []);
        private static Hash256 GetKey(in long blockNumber) => Keccak.Compute("Validators" + blockNumber);

        public ValidatorStore([KeyFilter(DbNames.BlockInfos)] IDb db)
        {
            _db = db;
            _latestFinalizedValidatorsBlockNumber = _db.GetLongFromBigEndianByteArrayWithoutLeadingZeros(LatestFinalizedValidatorsBlockNumberKey, EmptyBlockNumber);
        }

        public void SetValidators(long finalizingBlockNumber, Address[] validators)
        {
            if (finalizingBlockNumber > _latestFinalizedValidatorsBlockNumber)
            {
                ValidatorInfo validatorInfo = new(finalizingBlockNumber, _latestFinalizedValidatorsBlockNumber, validators);
                Rlp rlp = Rlp.Encode(validatorInfo);
                _db.Set(GetKey(finalizingBlockNumber), rlp.Bytes);
                _db.PutSpan(LatestFinalizedValidatorsBlockNumberKey.Bytes, finalizingBlockNumber.ToBigEndianSpanWithoutLeadingZeros(out _));
                _latestFinalizedValidatorsBlockNumber = finalizingBlockNumber;
                _latestValidatorInfo = validatorInfo;
                Metrics.ValidatorsCount = validators.Length;
            }
        }


        public Address[] GetValidators(in long? blockNumber = null) => blockNumber is null || blockNumber > _latestFinalizedValidatorsBlockNumber
                ? GetLatestValidatorInfo().Validators
                : FindValidatorInfo(blockNumber.Value).Validators;

        public ValidatorInfo GetValidatorsInfo(in long? blockNumber = null) => blockNumber is null || blockNumber > _latestFinalizedValidatorsBlockNumber
                ? GetLatestValidatorInfo()
                : FindValidatorInfo(blockNumber.Value);

        public PendingValidators PendingValidators
        {
            get
            {
                RlpReader ctx = new(_db.Get(PendingValidatorsKey) ?? Rlp.OfEmptyList.Bytes);
                return PendingValidatorsDecoder.Decode(ref ctx);
            }
            set => StorePendingValidators(value);
        }

        private void StorePendingValidators(PendingValidators value)
        {
            using ArrayPoolSpan<byte> rlp = PendingValidatorsDecoder.EncodeToArrayPoolSpan(value);
            _db.PutSpan(PendingValidatorsKey.Bytes, rlp);
        }

        private ValidatorInfo FindValidatorInfo(in long blockNumber)
        {
            ValidatorInfo currentValidatorInfo = GetLatestValidatorInfo();
            while (currentValidatorInfo.FinalizingBlockNumber >= blockNumber)
            {
                currentValidatorInfo = LoadValidatorInfo(currentValidatorInfo.PreviousFinalizingBlockNumber);
            }

            return currentValidatorInfo;
        }

        private ValidatorInfo GetLatestValidatorInfo()
        {
            ValidatorInfo info = _latestValidatorInfo ??= LoadValidatorInfo(_latestFinalizedValidatorsBlockNumber);
            Metrics.ValidatorsCount = info.Validators.Length;
            return info;
        }

        private ValidatorInfo LoadValidatorInfo(in long blockNumber)
        {
            if (blockNumber > EmptyBlockNumber)
            {
                Span<byte> bytes = _db.Get(GetKey(blockNumber));

                return bytes.IsEmpty
                    ? throw new InvalidOperationException($"No validator info for block number {blockNumber}.")
                    : Rlp.Decode<ValidatorInfo>(bytes);
            }

            return EmptyValidatorInfo;
        }
    }
}
