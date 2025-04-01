using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Logging;

namespace Nethermind.EngineApiProxy.Models
{
    /// <summary>
    /// Tracks PayloadIDs and their associated block hashes to manage engine_getPayload requests
    /// </summary>
    public class PayloadTracker
    {
        private readonly ILogger _logger;
        private readonly ConcurrentDictionary<Hash256, string> _headBlockToPayloadId = new();
        private readonly ConcurrentDictionary<string, Hash256> _payloadIdToHeadBlock = new();
        
        public PayloadTracker(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        /// <summary>
        /// Stores a mapping between a head block hash and a Payload ID
        /// </summary>
        /// <param name="headBlockHash">The hash of the head block from forkChoiceUpdated</param>
        /// <param name="payloadId">The Payload ID returned from the execution client</param>
        public void TrackPayload(Hash256 headBlockHash, string payloadId)
        {
            if (headBlockHash == null || string.IsNullOrEmpty(payloadId))
            {
                _logger.Error($"Cannot track null payload or hash. Hash: {headBlockHash}, PayloadId: {payloadId}");
                return;
            }
            
            _headBlockToPayloadId[headBlockHash] = payloadId;
            _payloadIdToHeadBlock[payloadId] = headBlockHash;
            
            _logger.Debug($"Tracking payload {payloadId} for head block {headBlockHash}");
        }
        
        /// <summary>
        /// Gets the Payload ID associated with a head block hash
        /// </summary>
        /// <param name="headBlockHash">The hash of the head block</param>
        /// <returns>The associated Payload ID or null if not found</returns>
        public string? GetPayloadId(Hash256 headBlockHash)
        {
            if (_headBlockToPayloadId.TryGetValue(headBlockHash, out var payloadId))
            {
                return payloadId;
            }
            
            return null;
        }
        
        /// <summary>
        /// Gets the head block hash associated with a Payload ID
        /// </summary>
        /// <param name="payloadId">The Payload ID</param>
        /// <returns>The associated head block hash or null if not found</returns>
        public Hash256? GetHeadBlock(string payloadId)
        {
            if (_payloadIdToHeadBlock.TryGetValue(payloadId, out var headBlockHash))
            {
                return headBlockHash;
            }
            
            return null;
        }
        
        /// <summary>
        /// Removes a Payload ID and its associated head block hash from tracking
        /// </summary>
        /// <param name="payloadId">The Payload ID to remove</param>
        public void RemovePayload(string payloadId)
        {
            if (_payloadIdToHeadBlock.TryRemove(payloadId, out var headBlockHash))
            {
                _headBlockToPayloadId.TryRemove(headBlockHash, out _);
                _logger.Debug($"Removed tracking for payload {payloadId}");
            }
        }
        
        /// <summary>
        /// Removes a head block hash and its associated Payload ID from tracking
        /// </summary>
        /// <param name="headBlockHash">The head block hash to remove</param>
        public void RemoveHeadBlock(Hash256 headBlockHash)
        {
            if (_headBlockToPayloadId.TryRemove(headBlockHash, out var payloadId))
            {
                _payloadIdToHeadBlock.TryRemove(payloadId, out _);
                _logger.Debug($"Removed tracking for head block {headBlockHash}");
            }
        }
        
        /// <summary>
        /// Clears all tracked Payload IDs and head block hashes
        /// </summary>
        public void Clear()
        {
            _headBlockToPayloadId.Clear();
            _payloadIdToHeadBlock.Clear();
            _logger.Debug("Cleared all payload tracking");
        }
    }
} 