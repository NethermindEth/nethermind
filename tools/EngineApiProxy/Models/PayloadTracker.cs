using System.Collections.Concurrent;
using Nethermind.Core.Crypto;
using Nethermind.Core.Extensions;
using Nethermind.Logging;

namespace Nethermind.EngineApiProxy.Models
{
    /// <summary>
    /// Tracks PayloadIDs and their associated block hashes to manage engine_getPayload requests
    /// </summary>
    public class PayloadTracker(ILogManager logManager)
    {
        private readonly ILogger _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        private readonly ConcurrentDictionary<Hash256, string> _headBlockToPayloadId = new();
        private readonly ConcurrentDictionary<string, Hash256> _payloadIdToHeadBlock = new();
        private readonly ConcurrentDictionary<Hash256, string> _headBlockToParentBeaconBlockRoot = new();

        /// <summary>
        /// Gets the most recently tracked payload ID
        /// </summary>
        public string LastTrackedPayloadId { get; private set; } = string.Empty;

        /// <summary>
        /// Gets the most recently tracked block hash as a hex string
        /// </summary>
        public string LastTrackedBlockHash { get; private set; } = string.Empty;

        /// <summary>
        /// Stores a mapping between a head block hash and a Payload ID
        /// </summary>
        /// <param name="headBlockHash">The hash of the head block from forkChoiceUpdated</param>
        /// <param name="payloadId">The Payload ID returned from the execution client</param>
        /// <param name="parentBeaconBlockRoot">Optional parent beacon block root to store with this head block</param>
        public void TrackPayload(Hash256 headBlockHash, string payloadId, string? parentBeaconBlockRoot = null)
        {
            if (headBlockHash == null || string.IsNullOrEmpty(payloadId))
            {
                _logger.Error($"Cannot track null payload or hash. Hash: {headBlockHash}, PayloadId: {payloadId}");
                return;
            }
            
            _headBlockToPayloadId[headBlockHash] = payloadId;
            _payloadIdToHeadBlock[payloadId] = headBlockHash;
            
            // Store the parent beacon block root if provided
            if (!string.IsNullOrEmpty(parentBeaconBlockRoot))
            {
                _headBlockToParentBeaconBlockRoot[headBlockHash] = parentBeaconBlockRoot;
                _logger.Debug($"Tracking parentBeaconBlockRoot {parentBeaconBlockRoot} for head block {headBlockHash}");
            }
            
            LastTrackedPayloadId = payloadId;
            LastTrackedBlockHash = headBlockHash.ToString();
            
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
        /// Tries to get the Payload ID associated with a head block hash
        /// </summary>
        /// <param name="headBlockHash">The hash of the head block</param>
        /// <param name="payloadId">The associated Payload ID if found</param>
        /// <returns>True if a Payload ID was found, false otherwise</returns>
        public bool TryGetPayloadId(Hash256 headBlockHash, out string? payloadId)
        {
            if (headBlockHash == null)
            {
                payloadId = default;
                return false;
            }
            return _headBlockToPayloadId.TryGetValue(headBlockHash, out payloadId);
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
                
                // Also remove from parent beacon block root mapping
                _headBlockToParentBeaconBlockRoot.TryRemove(headBlockHash, out _);
                
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
            _headBlockToParentBeaconBlockRoot.Clear();
            _logger.Debug("Cleared all payload tracking");
        }
        
        /// <summary>
        /// Checks if a block hash is being tracked
        /// </summary>
        /// <param name="blockHash">The block hash to check</param>
        /// <returns>True if the block hash is being tracked, false otherwise</returns>
        public bool IsPayloadTracked(Hash256 blockHash)
        {
            if (blockHash == null)
            {
                return false;
            }
            return _headBlockToPayloadId.ContainsKey(blockHash);
        }
        
        /// <summary>
        /// Registers a new payload block from engine_newPayload request
        /// </summary>
        /// <param name="blockHash">The hash of the new block</param>
        /// <param name="parentHash">The parent hash of the new block</param>
        public void RegisterNewPayload(string blockHash, string parentHash)
        {
            if (string.IsNullOrEmpty(blockHash) || string.IsNullOrEmpty(parentHash))
            {
                _logger.Error($"Cannot register payload with null/empty hashes. Block: {blockHash}, Parent: {parentHash}");
                return;
            }
            
            // For now, we just log the registration - in future we could store a mapping
            // between blocks and their parents for optimized validation in merged mode
            LastTrackedBlockHash = blockHash;
            _logger.Debug($"Registered new payload block {blockHash} with parent {parentHash}");
        }

        /// <summary>
        /// Gets the parent beacon block root associated with a head block hash
        /// </summary>
        /// <param name="headBlockHash">The hash of the head block</param>
        /// <returns>The associated parent beacon block root or null if not found</returns>
        public string? GetParentBeaconBlockRoot(Hash256 headBlockHash)
        {
            if (_headBlockToParentBeaconBlockRoot.TryGetValue(headBlockHash, out var parentBeaconBlockRoot))
            {
                return parentBeaconBlockRoot;
            }
            
            return null;
        }
        
        /// <summary>
        /// Associates a parent beacon block root with a head block hash
        /// </summary>
        /// <param name="headBlockHash">The hash of the head block</param>
        /// <param name="parentBeaconBlockRoot">The parent beacon block root to associate</param>
        /// <returns>True if the association was successful, false otherwise</returns>
        public bool AssociateParentBeaconBlockRoot(Hash256 headBlockHash, string parentBeaconBlockRoot)
        {
            if (headBlockHash == null || string.IsNullOrEmpty(parentBeaconBlockRoot))
            {
                _logger.Error($"Cannot associate null/empty values. Hash: {headBlockHash}, ParentBeaconBlockRoot: {parentBeaconBlockRoot}");
                return false;
            }
            
            _headBlockToParentBeaconBlockRoot[headBlockHash] = parentBeaconBlockRoot;
            _logger.Debug($"Associated parentBeaconBlockRoot {parentBeaconBlockRoot} with head block {headBlockHash}");
            return true;
        }
        
        /// <summary>
        /// Tries to get the parent beacon block root associated with a head block hash
        /// </summary>
        /// <param name="headBlockHash">The hash of the head block</param>
        /// <param name="parentBeaconBlockRoot">The associated parent beacon block root if found</param>
        /// <returns>True if a parent beacon block root was found, false otherwise</returns>
        public bool TryGetParentBeaconBlockRoot(Hash256 headBlockHash, out string? parentBeaconBlockRoot)
        {
            if (headBlockHash == null)
            {
                parentBeaconBlockRoot = default;
                return false;
            }
            return _headBlockToParentBeaconBlockRoot.TryGetValue(headBlockHash, out parentBeaconBlockRoot);
        }
    }
} 