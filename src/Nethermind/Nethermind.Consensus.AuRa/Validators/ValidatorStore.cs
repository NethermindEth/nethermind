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
        private ulong _latestFinalizedValidatorsBlockNumber;
        private ValidatorInfo? _latestValidatorInfo;
        private static readonly ulong EmptyBlockNumber = ulong.MaxValue;
        private static readonly ValidatorInfo EmptyValidatorInfo = new(ulong.MaxValue, ulong.MaxValue, []);
        private static Hash256 GetKey(in ulong blockNumber) => Keccak.Compute("Validators" + blockNumber);

        public ValidatorStore([KeyFilter(DbNames.BlockInfos)] IDb db)
        {
            _db = db;
            _latestFinalizedValidatorsBlockNumber = _db.GetULongFromBigEndianByteArrayWithoutLeadingZeros(LatestFinalizedValidatorsBlockNumberKey, EmptyBlockNumber);
        }

        public void SetValidators(ulong finalizingBlockNumber, Address[] validators)
        {
            if (_latestFinalizedValidatorsBlockNumber == EmptyBlockNumber || finalizingBlockNumber > _latestFinalizedValidatorsBlockNumber)
            {
                ValidatorInfo validatorInfo = new(finalizingBlockNumber, _latestFinalizedValidatorsBlockNumber == EmptyBlockNumber ? EmptyBlockNumber : _latestFinalizedValidatorsBlockNumber, validators);
                Rlp rlp = Rlp.Encode(validatorInfo);
                _db.Set(GetKey(finalizingBlockNumber), rlp.Bytes);
                _db.PutSpan(LatestFinalizedValidatorsBlockNumberKey.Bytes, finalizingBlockNumber.ToBigEndianSpanWithoutLeadingZeros(out _));
                _latestFinalizedValidatorsBlockNumber = finalizingBlockNumber;
                _latestValidatorInfo = validatorInfo;
                Metrics.ValidatorsCount = validators.Length;
            }
        }


        public Address[] GetValidators(in ulong? blockNumber = null) => blockNumber is null || _latestFinalizedValidatorsBlockNumber == EmptyBlockNumber || blockNumber > _latestFinalizedValidatorsBlockNumber
                ? GetLatestValidatorInfo().Validators
                : FindValidatorInfo(blockNumber.Value).Validators;

        public ValidatorInfo GetValidatorsInfo(in ulong? blockNumber = null) => blockNumber is null || _latestFinalizedValidatorsBlockNumber == EmptyBlockNumber || blockNumber > _latestFinalizedValidatorsBlockNumber
                ? GetLatestValidatorInfo()
                : FindValidatorInfo(blockNumber.Value);

        public PendingValidators? PendingValidators
        {
            get
            {
                RlpReader ctx = new(_db.Get(PendingValidatorsKey) ?? Rlp.OfEmptyList.Bytes);
                return PendingValidatorsDecoder.Decode(ref ctx);
            }
            set => StorePendingValidators(value);
        }

        private void StorePendingValidators(PendingValidators? value)
        {
            using ArrayPoolSpan<byte> rlp = PendingValidatorsDecoder.EncodeToArrayPoolSpan(value);
            _db.PutSpan(PendingValidatorsKey.Bytes, rlp);
        }

        private ValidatorInfo FindValidatorInfo(in ulong blockNumber)
        {
            ValidatorInfo currentValidatorInfo = GetLatestValidatorInfo();
            while (currentValidatorInfo.FinalizingBlockNumber >= blockNumber && currentValidatorInfo.PreviousFinalizingBlockNumber != EmptyBlockNumber)
            {
                currentValidatorInfo = LoadValidatorInfo(currentValidatorInfo.PreviousFinalizingBlockNumber);
            }

            return currentValidatorInfo.FinalizingBlockNumber >= blockNumber ? EmptyValidatorInfo : currentValidatorInfo;
        }

        private ValidatorInfo GetLatestValidatorInfo()
        {
            ValidatorInfo info = _latestValidatorInfo ??= LoadValidatorInfo(_latestFinalizedValidatorsBlockNumber);
            Metrics.ValidatorsCount = info.Validators.Length;
            return info;
        }

        private ValidatorInfo LoadValidatorInfo(in ulong blockNumber)
        {
            if (blockNumber != EmptyBlockNumber)
            {
                Span<byte> bytes = _db.Get(GetKey(blockNumber));

                return bytes.IsEmpty
                    ? throw new InvalidOperationException($"No validator info for block number {blockNumber}.")
                    : Rlp.Decode<ValidatorInfo>(bytes) ?? throw new RlpException("Validator info decoding returned null.");
            }

            return EmptyValidatorInfo;
        }
    }
}
