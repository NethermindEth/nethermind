// SPDX-FileCopyrightText: 2022 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using Nethermind.Core;
using Nethermind.Core.Specs;
using Nethermind.Logging;

namespace Nethermind.TxPool.Filters
{
	/// <summary>
	/// Ignores transactions that outright exceed block gas limit or configured max block gas limit.
	/// </summary>
	internal sealed class GasLimitTxFilter(
			IChainHeadSpecProvider specProvider,
			IChainHeadInfoProvider chainHeadInfoProvider,
			ITxPoolConfig txPoolConfig,
			ILogger logger) : IIncomingTxFilter
	{
		private readonly IChainHeadSpecProvider _specProvider = specProvider;
		private readonly IChainHeadInfoProvider _chainHeadInfoProvider = chainHeadInfoProvider;
		private readonly ILogger _logger = logger;
		private readonly long _configuredGasLimit = txPoolConfig.GasLimit ?? long.MaxValue;

		public AcceptTxResult Accept(Transaction tx, ref TxFilteringState state, TxHandlingOptions handlingOptions)
		{
			long txGasLimitCap = GetTxGasLimitCap();

			if (tx.GasLimit > txGasLimitCap)
			{
				Metrics.PendingTransactionsGasLimitTooHigh++;

				if (_logger.IsTrace)
				{
					_logger.Trace($"Skipped adding transaction {tx.ToString("  ")}, gas limit exceeded.");
				}

				bool isNotLocal = (handlingOptions & TxHandlingOptions.PersistentBroadcast) == 0;
				return isNotLocal ?
					AcceptTxResult.GasLimitExceeded :
					AcceptTxResult.GasLimitExceeded.WithMessage($"Tx gas limit cap: {txGasLimitCap}, gas limit of rejected tx: {tx.GasLimit}");
			}

			return AcceptTxResult.Accepted;
		}

		private long GetTxGasLimitCap()
		{
			IReleaseSpec spec = _specProvider.GetCurrentHeadSpec();

			long gasLimit = Math.Min(_chainHeadInfoProvider.BlockGasLimit ?? long.MaxValue, _configuredGasLimit);
			if (spec.IsEip7825Enabled)
			{
				gasLimit = Math.Min(gasLimit, Eip7825Constants.GetTxGasLimitCap(spec));
			}

			return gasLimit;
		}
    }
}
