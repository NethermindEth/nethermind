// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Crypto;
using Nethermind.Serialization.Rlp;

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class DepositApproval
    {
        public Keccak Id { get; private set; }
        public Keccak AssetId { get; private set; }
        public string AssetName { get; private set; }
        public string Kyc { get; private set; }
        public Address Consumer { get; private set; }
        public Address Provider { get; private set; }
        public ulong Timestamp { get; private set; }
        public DepositApprovalState State { get; private set; }

        public static Keccak CalculateId(Keccak assetId, Address consumer)
        {
            return Keccak.Compute(Rlp.Encode(Rlp.Encode(assetId), Rlp.Encode(consumer)).Bytes);
        }

        public DepositApproval(
            Keccak assetId,
            string assetName,
            string kyc,
            Address consumer,
            Address provider,
            ulong timestamp,
            DepositApprovalState state)
        {
            AssetId = assetId ?? throw new ArgumentNullException(nameof(assetId));
            AssetName = assetName ?? throw new ArgumentNullException(nameof(assetName));
            Kyc = kyc ?? throw new ArgumentNullException(nameof(kyc));
            Consumer = consumer ?? throw new ArgumentNullException(nameof(consumer));
            Provider = provider ?? throw new ArgumentNullException(nameof(provider));
            Timestamp = timestamp;
            State = state;
            Id = CalculateId(assetId, consumer);
        }

        public void Confirm()
        {
            State = DepositApprovalState.Confirmed;
        }

        public void Reject()
        {
            State = DepositApprovalState.Rejected;
        }
    }
}
