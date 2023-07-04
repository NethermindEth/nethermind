// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Db;
using Nethermind.Serialization.Rlp;

namespace Nethermind.Consensus.AuRa.Validators
{
    public class ValidatorStore : IValidatorStore
    {
        internal static readonly Keccak LatestFinalizedValidatorsBlockNumberKey = Keccak.Compute("LatestFinalizedValidatorsBlockNumber");
        internal static readonly Keccak PendingValidatorsKey = Keccak.Compute("PendingValidators");
        private static readonly PendingValidatorsDecoder PendingValidatorsDecoder = new PendingValidatorsDecoder();

        private readonly IDb _db;
        private long _latestFinalizedValidatorsBlockNumber;
        private ValidatorInfo _latestValidatorInfo;
        private static readonly int EmptyBlockNumber = -1;
        private static readonly ValidatorInfo EmptyValidatorInfo = new ValidatorInfo(-1, -1, Array.Empty<Address>());
        private static Keccak GetKey(in long blockNumber) => Keccak.Compute("Validators" + blockNumber);

        public ValidatorStore(IDb db)
        {
            _db = db;
            _latestFinalizedValidatorsBlockNumber = _db.Get(LatestFinalizedValidatorsBlockNumberKey)?.ToLongFromBigEndianByteArrayWithoutLeadingZeros() ?? EmptyBlockNumber;
        }

        public void SetValidators(long finalizingBlockNumber, Address[] validators)
        {
            if (finalizingBlockNumber > _latestFinalizedValidatorsBlockNumber)
            {
                var validatorInfo = new ValidatorInfo(finalizingBlockNumber, _latestFinalizedValidatorsBlockNumber, validators);
                var rlp = Rlp.Encode(validatorInfo);
                _db.Set(GetKey(finalizingBlockNumber), rlp.Bytes);
                _db.Set(LatestFinalizedValidatorsBlockNumberKey, finalizingBlockNumber.ToBigEndianByteArrayWithoutLeadingZeros());
                _latestFinalizedValidatorsBlockNumber = finalizingBlockNumber;
                _latestValidatorInfo = validatorInfo;
                Metrics.ValidatorsCount = validators.Length;
            }
        }


        public Address[] GetValidators(in long? blockNumber = null)
        {
            return blockNumber == null || blockNumber > _latestFinalizedValidatorsBlockNumber
                ? GetLatestValidatorInfo().Validators
                : FindValidatorInfo(blockNumber.Value).Validators;
        }

        public ValidatorInfo GetValidatorsInfo(in long? blockNumber = null)
        {
            return blockNumber == null || blockNumber > _latestFinalizedValidatorsBlockNumber
                ? GetLatestValidatorInfo()
                : FindValidatorInfo(blockNumber.Value);
        }

        public PendingValidators PendingValidators
        {
            get
            {
                var rlpStream = new RlpStream(_db.Get(PendingValidatorsKey) ?? Rlp.OfEmptySequence.Bytes);
                return PendingValidatorsDecoder.Decode(rlpStream);
            }
            set => _db.Set(PendingValidatorsKey, PendingValidatorsDecoder.Encode(value).Bytes);
        }

        private ValidatorInfo FindValidatorInfo(in long blockNumber)
        {
            var currentValidatorInfo = GetLatestValidatorInfo();
            while (currentValidatorInfo.FinalizingBlockNumber >= blockNumber)
            {
                currentValidatorInfo = LoadValidatorInfo(currentValidatorInfo.PreviousFinalizingBlockNumber);
            }

            return currentValidatorInfo;
        }

        private ValidatorInfo GetLatestValidatorInfo()
        {
            var info = _latestValidatorInfo ??= LoadValidatorInfo(_latestFinalizedValidatorsBlockNumber);
            Metrics.ValidatorsCount = info.Validators.Length;
            return info;
        }

        private ValidatorInfo LoadValidatorInfo(in long blockNumber)
        {
            if (blockNumber > EmptyBlockNumber)
            {
                var bytes = _db.Get(GetKey(blockNumber));
                return bytes is not null ? Rlp.Decode<ValidatorInfo>(bytes) : null;
            }

            return EmptyValidatorInfo;
        }
    }
}
