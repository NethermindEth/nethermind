//  Copyright (c) 2018 Demerzel Solutions Limited
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

namespace Nethermind.DataMarketplace.Core.Domain
{
    public class FaucetResponse : IEquatable<FaucetResponse>
    {
        public FaucetRequestStatus Status { get; }
        public FaucetRequestDetails LatestRequest { get; }

        public FaucetResponse(FaucetRequestStatus status, FaucetRequestDetails? latestRequest = null)
        {
            Status = status;
            LatestRequest = latestRequest ?? FaucetRequestDetails.Empty;
        }

        public static FaucetResponse FaucetNotSet => new FaucetResponse(FaucetRequestStatus.FaucetNotSet);
        public static FaucetResponse FaucetDisabled => new FaucetResponse(FaucetRequestStatus.FaucetDisabled);
        public static FaucetResponse FaucetAddressNotSet => new FaucetResponse(FaucetRequestStatus.FaucetAddressNotSet);
        public static FaucetResponse InvalidNodeAddress => new FaucetResponse(FaucetRequestStatus.InvalidNodeAddress);
        public static FaucetResponse SameAddressAsFaucet => new FaucetResponse(FaucetRequestStatus.SameAddressAsFaucet);
        public static FaucetResponse ZeroValue => new FaucetResponse(FaucetRequestStatus.ZeroValue);
        public static FaucetResponse TooBigValue => new FaucetResponse(FaucetRequestStatus.TooBigValue);

        public static FaucetResponse DailyRequestsTotalValueReached =>
            new FaucetResponse(FaucetRequestStatus.DailyRequestsTotalValueReached);

        public static FaucetResponse RequestAlreadyProcessing =>
            new FaucetResponse(FaucetRequestStatus.RequestAlreadyProcessing);

        public static FaucetResponse RequestAlreadyProcessedToday(FaucetRequestDetails request) =>
            new FaucetResponse(FaucetRequestStatus.RequestAlreadyProcessedToday, request);

        public static FaucetResponse RequestError => new FaucetResponse(FaucetRequestStatus.RequestError);

        public static FaucetResponse RequestCompleted(FaucetRequestDetails request)
            => new FaucetResponse(FaucetRequestStatus.RequestCompleted, request);

        public static FaucetResponse ProcessingRequestError =>
            new FaucetResponse(FaucetRequestStatus.ProcessingRequestError);

        public bool Equals(FaucetResponse? other)
        {
            if (ReferenceEquals(null, other)) return false;
            if (ReferenceEquals(this, other)) return true;
            return Status == other.Status && Equals(LatestRequest, other.LatestRequest);
        }

        public override bool Equals(object? obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((FaucetResponse) obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((int) Status * 397) ^ (LatestRequest != null ? LatestRequest.GetHashCode() : 0);
            }
        }
    }
}
