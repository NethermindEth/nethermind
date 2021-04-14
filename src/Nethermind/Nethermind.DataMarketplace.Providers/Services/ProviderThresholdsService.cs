using System;
using System.Threading.Tasks;
using Nethermind.DataMarketplace.Core.Configs;
using Nethermind.DataMarketplace.Core.Services;
using Nethermind.Int256;
using Nethermind.Logging;

namespace Nethermind.DataMarketplace.Providers.Services
{
    class ProviderThresholdsService : IProviderThresholdsService
    {
        private readonly IConfigManager _configManager;
        private readonly string _configId;
        private readonly ILogger _logger;

        public ProviderThresholdsService(IConfigManager configManager, string configId, ILogManager logManager)
        {
            _configManager = configManager ?? throw new ArgumentNullException(nameof(configManager));
            _configId = configId ?? throw new ArgumentNullException(nameof(configId));
            _logger = logManager.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }

        public async Task<UInt256> GetCurrentReceiptRequestAsync()
        {
            NdmConfig? config = await _configManager.GetAsync(_configId);
            return config?.ReceiptRequestThreshold ?? 10000000000000000;
        }

        public async Task<UInt256> GetCurrentReceiptsMergeAsync()
        {
            NdmConfig? config = await _configManager.GetAsync(_configId);
            return config?.ReceiptsMergeThreshold ?? 100000000000000000;
        }

        public async Task<UInt256> GetCurrentPaymentClaimAsync()
        {
            NdmConfig? config = await _configManager.GetAsync(_configId);
            return config?.PaymentClaimThreshold ?? 1000000000000000000;
        }

        public async Task SetReceiptRequestAsync(UInt256 value)
        {
            if (value <= 0)
            {
                throw new ArgumentException("Receipt request threshold must be greater than 0.");
            }

            NdmConfig? config = await _configManager.GetAsync(_configId);
            if (config == null)
            {
                if (_logger.IsError) _logger.Error($"Failed to retrieve config {_configId} to update receipt request threshold.");
                throw new InvalidOperationException($"Failed to retrieve config {_configId} to update receipt request threshold.");
            }

            config.ReceiptRequestThreshold = value;
            await _configManager.UpdateAsync(config);
            if (_logger.IsInfo) _logger.Info($"Updated receipt request threshold: {config.ReceiptRequestThreshold} wei.");
        }

        public async Task SetReceiptsMergeAsync(UInt256 value)
        {
            if (value <= 0)
            {
                throw new ArgumentException("Receipts merge threshold must be greater than 0.");
            }

            NdmConfig? config = await _configManager.GetAsync(_configId);
            if (config == null)
            {
                if (_logger.IsError) _logger.Error($"Failed to retrieve config {_configId} to update receipts merge threshold.");
                throw new InvalidOperationException($"Failed to retrieve config {_configId} to update receipts merge threshold.");
            }

            config.ReceiptsMergeThreshold = value;
            await _configManager.UpdateAsync(config);
            if (_logger.IsInfo) _logger.Info($"Updated receipts merge threshold: {config.ReceiptsMergeThreshold} wei.");
        }

        public async Task SetPaymentClaimAsync(UInt256 value)
        {
            if (value <= 0)
            {
                throw new ArgumentException("Payment claim threshold must be greater than 0.");
            }

            NdmConfig? config = await _configManager.GetAsync(_configId);
            if (config == null)
            {
                if (_logger.IsError) _logger.Error($"Failed to retrieve config {_configId} to update payment claim threshold.");
                throw new InvalidOperationException($"Failed to retrieve config {_configId} to update payment claim threshold.");
            }

            config.PaymentClaimThreshold = value;
            await _configManager.UpdateAsync(config);
            if (_logger.IsInfo) _logger.Info($"Updated payment claim threshold: {config.PaymentClaimThreshold} wei.");
        }
    }
}