// SPDX-FileCopyrightText: 2023 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nethermind.Abi;
using Nethermind.Blockchain;
using Nethermind.Consensus.Transactions;
using Nethermind.Core;
using Nethermind.Int256;

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
        private long _sentReportsInBlock = 0;

        public IEnumerable<Transaction> GetTransactions(BlockHeader parent, long gasLimit)
        {
            foreach (var transaction in _contractValidator.GetTransactions(parent, gasLimit))
            {
                yield return transaction;
            }

            long currentBlockNumber = parent.Number + 1;

            if (_contractValidator.ForSealing && IsPosdao(currentBlockNumber))
            {
                FilterReports(parent);

                foreach (Transaction tx in GetPersistentReportsTransactions(currentBlockNumber).Take(MaxReportsPerBlock))
                {
                    yield return tx;
                }
            }
        }

        private IEnumerable<Transaction> GetPersistentReportsTransactions(long currentBlockNumber)
        {
            foreach (PersistentReport persistentReport in _persistentReports)
            {
                // we want to wait at least 1 block with persistent reports to avoid duplicates
                if (persistentReport.BlockNumber + ReportsSkipBlocks < currentBlockNumber)
                {
                    yield return ValidatorContract.ReportMalicious(persistentReport.MaliciousValidator, persistentReport.BlockNumber, persistentReport.Proof);
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

                if (blockNumber > _sentReportsInBlock + ReportsSkipBlocks)
                {
                    _sentReportsInBlock = blockNumber;
                    foreach (var persistentReport in _persistentReports)
                    {
                        try
                        {
                            SendTransaction(ReportType.Malicious, _posdaoTxSender, CreateReportMaliciousTransactionCore(persistentReport));
                        }
                        catch (AbiException e)
                        {
                            if (_logger.IsWarn) _logger.Warn($"Cannot report validator {persistentReport.MaliciousValidator} for misbehavior on block {persistentReport.BlockNumber}: {e.Message}.");
                        }
                    }
                }
            }
        }

        private void FilterReports(BlockHeader parent)
        {
            var node = _persistentReports.First;
            while (node is not null)
            {
                var next = node.Next;
                var persistentReport = node.Value;

                if (_logger.IsTrace) _logger.Trace($"Checking if report of malicious validator {persistentReport.MaliciousValidator} at block {persistentReport.BlockNumber} should be removed from cache.");

                try
                {
                    if (!_contractValidator.ValidatorContract.ShouldValidatorReport(parent, ValidatorContract.NodeAddress, persistentReport.MaliciousValidator, persistentReport.BlockNumber))
                    {
                        _persistentReports.Remove(node);
                        if (_logger.IsTrace) _logger.Trace($"Successfully removed report of malicious validator {persistentReport.MaliciousValidator} at block {persistentReport.BlockNumber} from report cache.");
                    }
                }
                catch (AbiException e)
                {
                    if (_logger.IsError) _logger.Error($"Failed to query report status, dropping pending report of malicious validator {persistentReport.MaliciousValidator} at block {persistentReport.BlockNumber}. {new StackTrace()}", e);
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
            public Address MaliciousValidator { get; }
            public UInt256 BlockNumber { get; }
            public byte[] Proof { get; }

            public PersistentReport(Address maliciousValidator, in UInt256 blockNumber, byte[] proof)
            {
                MaliciousValidator = maliciousValidator;
                BlockNumber = blockNumber;
                Proof = proof;
            }

            public bool Equals(PersistentReport other)
            {
                if (ReferenceEquals(null, other)) return false;
                if (ReferenceEquals(this, other)) return true;
                return Equals(MaliciousValidator, other.MaliciousValidator) && BlockNumber == other.BlockNumber;
            }

            public override bool Equals(object obj)
            {
                if (ReferenceEquals(null, obj)) return false;
                if (ReferenceEquals(this, obj)) return true;
                if (obj.GetType() != this.GetType()) return false;
                return Equals((PersistentReport)obj);
            }

            public override int GetHashCode() => HashCode.Combine(MaliciousValidator, BlockNumber);
        }
    }
}
