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

using System.Collections.Generic;
using System.Linq;
using Nethermind.DataMarketplace.Core.Domain;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Consumers.Receipts.Services
{
    public class ReceiptRequestValidator : IReceiptRequestValidator
    {
        private readonly ILogger _logger;

        public ReceiptRequestValidator(ILogManager logManager)
        {
            _logger = logManager.GetClassLogger();
        }

        public bool IsValid(DataDeliveryReceiptRequest receiptRequest, long unpaidUnits, long consumedUnits,
            long purchasedUnits)
        {
            var requestedUnitsRange = receiptRequest.UnitsRange;
            var from = requestedUnitsRange.From;
            var to = requestedUnitsRange.To;
            var requestedUnits = to - from + 1;
            var isMerged = receiptRequest.ReceiptsToMerge.Any();
            if (_logger.IsInfo) _logger.Info($"Requested units range{(isMerged ? " (merged)" : string.Empty)}: [{from}, {to}] for deposit: '{receiptRequest.DepositId}'. Unpaid units: {unpaidUnits}, requested: {requestedUnits}.");
            if (requestedUnits > purchasedUnits || to >= purchasedUnits)
            {
                if (_logger.IsWarn) _logger.Warn($"Invalid receipt request, requested units: {requestedUnits}, purchased: {purchasedUnits} <= {to} (range to).");
                
                return false;
            }

            if (requestedUnits <= unpaidUnits)
            {
                return true;
            }
            
            if (!isMerged)
            {
                var consumedAndUnpaidUnits = consumedUnits + unpaidUnits;
                if (_logger.IsInfo) _logger.Info($"No merged receipts provided for deposit: '{receiptRequest.DepositId}', validating: {requestedUnits} <= {unpaidUnits} OR {from} == 0 AND {requestedUnits} <= {consumedAndUnpaidUnits}");

                return requestedUnits <= unpaidUnits || from == 0 && requestedUnits <= consumedAndUnpaidUnits;
            }

            var subsets = new List<UnitsRange>();
            var subsetsRange = 0L;

            var receiptsToMerge = receiptRequest.ReceiptsToMerge.ToArray();
            if (receiptsToMerge.Length == 1)
            {
                var previousRange = receiptsToMerge[0].UnitsRange;
                if (_logger.IsInfo) _logger.Info($"Previous units range: [{previousRange.From}, {previousRange.To}] for deposit: '{receiptRequest.DepositId}'.");

                if (receiptRequest.UnitsRange.Equals(previousRange))
                {
                    return true;
                }

                return previousRange.To - previousRange.From + 1 + unpaidUnits >= requestedUnits
                       && ((requestedUnitsRange.To > previousRange.To
                               ? requestedUnitsRange.To - previousRange.To
                               : previousRange.To - requestedUnitsRange.To) <= unpaidUnits);
            }

            for (var i = 0; i < receiptsToMerge.Length; i++)
            {
                var mergedUnitsRange = receiptsToMerge[i].UnitsRange;

                for (var j = 0; j < receiptsToMerge.Length; j++)
                {
                    if (i == j)
                    {
                        continue;
                    }
                    
                    var mergedUnitsRangeToCompare = receiptsToMerge[j].UnitsRange;
                    if (mergedUnitsRange.Equals(mergedUnitsRangeToCompare))
                    {
                        return false;
                    }
                    
                    if (mergedUnitsRange.IsSubsetOf(mergedUnitsRangeToCompare))
                    {
                        continue;
                    }

                    if (mergedUnitsRange.IntersectsWith(mergedUnitsRangeToCompare))
                    {
                        return false;
                    }
                }

                if (mergedUnitsRange.Equals(requestedUnitsRange))
                {
                    return false;
                }

                if (mergedUnitsRange.IsSubsetOf(requestedUnitsRange))
                {
                    subsets.Add(mergedUnitsRange);
                    subsetsRange += mergedUnitsRange.To - mergedUnitsRange.From + 1;
                    continue;
                }
                
                if (mergedUnitsRange.IntersectsWith(requestedUnitsRange))
                {
                    return false;
                }
            }
            
            if (!subsets.Any())
            {
                if (_logger.IsInfo) _logger.Info($"No merged receipts subsets found, validating: {requestedUnits} <= {unpaidUnits}.");
                
                return requestedUnits <= unpaidUnits;
            }

            if (_logger.IsInfo) _logger.Info($"Merged receipts subsets found, validating: {requestedUnits} - {subsetsRange} <= {unpaidUnits}.");

            return requestedUnits - subsetsRange <= unpaidUnits;
        }
    }
}