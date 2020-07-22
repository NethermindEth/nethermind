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
// 

using System;
using System.Collections.Generic;
using System.Linq;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Dirichlet.Numerics;

namespace Nethermind.Consensus.AuRa.Validators
{
    public partial class ReportingContractBasedValidator : ITxSource
    {
        /// <summary>
        /// The maximum number of reports to keep queued
        /// </summary>
        internal const int MaxQueuedReports = 10;
        
        /// <summary>
        /// The maximum number of malice reports to include when creating a new block.
        /// </summary>
        internal const int MaxReportsPerBlock = 10;
        
        /// <summary>
        /// Don't re-send malice reports every block. Skip this many before retrying.
        /// </summary>
        private const int ReportsSkipBlocks = 1;

        private readonly LinkedList<PersistentReport> _persistentReports;
        private long _resentReportsInBlock = 0;
        
        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            foreach (var transaction in _contractValidator.GetTransactions(parent, gasLimit))
            {
                if (gasLimit >= transaction.GasLimit)
                {
                    gasLimit -= transaction.GasLimit;
                    yield return transaction;
                }
            }

            if (_contractValidator.ForSealing && IsPosdao(parent.Number + 1))
            {
                FilterReports(parent);
                foreach (var persistentReport in _persistentReports.Take(MaxReportsPerBlock))
                {
                    var transaction = ValidatorContract.ReportMalicious(persistentReport.ValidatorAddress, persistentReport.BlockNumber, persistentReport.Proof);
                    if (transaction != null && gasLimit >= transaction.GasLimit)
                    {
                        gasLimit -= transaction.GasLimit;
                        yield return transaction;
                    }
                }
            }
        }
        
        private void ResendPersistedReports(BlockHeader blockHeader)
        {
            var blockNumber = blockHeader.Number;
            if (!IsPosdao(blockNumber))
            {
                if (_logger.IsTrace) _logger.Trace("Skipping resending of queued malicious behavior reports.");
            }
            else
            {
                FilterReports(blockHeader);
                TruncateReports();

                if (blockNumber > _resentReportsInBlock + ReportsSkipBlocks)
                {
                    _resentReportsInBlock = blockNumber;
                    foreach (var persistentReport in _persistentReports)
                    {
                        try
                        {
                            SendTransaction(ReportType.Malicious, _posdaoTxSender, CreateReportMaliciousTransactionCore(persistentReport));
                        }
                        catch (AuRaException e)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Cannot report validator {persistentReport.ValidatorAddress} for misbehavior on block {persistentReport.BlockNumber}: {e.Message}.");
                        }
                    }
                }
            }
        }

        private void FilterReports(BlockHeader parent)
        {
            var node = _persistentReports.First;
            while (node != null)
            {
                var next = node.Next;
                var persistentReport = node.Value;
                
                if (_logger.IsTrace) _logger.Trace($"Checking if report of malicious validator {persistentReport.ValidatorAddress} at block {persistentReport.BlockNumber} should be removed from cache.");

                try
                {
                    if (!_contractValidator.ValidatorContract.ShouldValidatorReport(ValidatorContract.NodeAddress, persistentReport.ValidatorAddress, persistentReport.BlockNumber, parent))
                    {
                        _persistentReports.Remove(node);
                        if (_logger.IsTrace) _logger.Trace("Successfully removed report from report cache.");
                    }
                }
                catch (AuRaException e)
                {
                    if (_logger.IsError) _logger.Error("Failed to query report status, dropping pending report.", e);
                    _persistentReports.Remove(node);
                }

                node = next;
            }
        }

        private void TruncateReports()
        {
            var toRemove = _persistentReports.Count - MaxQueuedReports;
            if (toRemove > 0)
            {
                if (_logger.IsWarn) _logger.Warn($"Removing {toRemove} reports from report cache, even though it has not been finalized.");
                
                for (int i = 0; i < toRemove; i++)
                {
                    _persistentReports.RemoveFirst();
                }
            }
        }
        public override string ToString() => $"{nameof(ReportingContractBasedValidator)} [ {_contractValidator} ]";

        internal class PersistentReport : IEquatable<PersistentReport>
        {
            public Address ValidatorAddress { get; }
            public UInt256 BlockNumber { get; }
            public byte[] Proof { get; }

            public PersistentReport(Address validatorAddress, UInt256 blockNumber, byte[] proof)
            {
                ValidatorAddress = validatorAddress;
                BlockNumber = blockNumber;
                Proof = proof;
            }

            public bool Equals(PersistentReport other)
            {
                if (ReferenceEquals(null, other)) return false;
                if (ReferenceEquals(this, other)) return true;
                return Equals(ValidatorAddress, other.ValidatorAddress) && BlockNumber == other.BlockNumber;
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                if (obj.GetType() != this.GetType()) return false;
                return Equals((PersistentReport) obj);
            }

            public override int GetHashCode() => HashCode.Combine(ValidatorAddress, BlockNumber);
        }
    }
}
