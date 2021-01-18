//  Copyright (c) 2021 Demerzel Solutions Limited
//  This file is part of the Nethermind library.
// 
//  The Nethermind library is free software: you can redistribute it and/or modify
//  it under the terms of the GNU Lesser General Public License as published by
//  the Free Software Foundation, either version 3 of the License, or
//  (at your option) any later version.
// 
//  The Nethermind library is distributed in the hope that it will be useful,
//  but WITHOUT ANY WARRANTY; without even the implied warranty of
//  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
//  GNU Lesser General Public License for more details.
// 
//  You should have received a copy of the GNU Lesser General Public License
//  along with the Nethermind. If not, see <http://www.gnu.org/licenses/>.

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

        private Keccak GetKey(in long blockNumber) => Keccak.Compute("Validators" + blockNumber);


        public Address[] GetValidators(long? blockNumber = null)
        {
            return blockNumber == null || blockNumber > _latestFinalizedValidatorsBlockNumber ? GetLatestValidatorInfo().Validators : FindValidatorInfo(blockNumber.Value);
        }

        public PendingValidators PendingValidators {
            get
            {
                var rlpStream = new RlpStream(_db.Get(PendingValidatorsKey) ?? Rlp.OfEmptySequence.Bytes);
                return PendingValidatorsDecoder.Decode(rlpStream);
            }
            set => _db.Set(PendingValidatorsKey,  PendingValidatorsDecoder.Encode(value).Bytes);
        }

        private Address[] FindValidatorInfo(in long blockNumber)
        {
            var currentValidatorInfo = GetLatestValidatorInfo();
            while (currentValidatorInfo.FinalizingBlockNumber >= blockNumber)
            {
                currentValidatorInfo = LoadValidatorInfo(currentValidatorInfo.PreviousFinalizingBlockNumber);
            }

            return currentValidatorInfo.Validators;
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
                return bytes != null ? Rlp.Decode<ValidatorInfo>(bytes) : null;
            }

            return EmptyValidatorInfo;
        }
    }
} 
