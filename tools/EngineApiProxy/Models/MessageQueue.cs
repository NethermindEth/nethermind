using System.Collections.Concurrent;
using Nethermind.Logging;

namespace Nethermind.EngineApiProxy.Models
{
    /// <summary>
    /// Manages the queuing and processing of intercepted messages in the proxy
    /// </summary>
    public class MessageQueue
    {
        private readonly ILogger _logger;
        private readonly ConcurrentQueue<QueuedMessage> _messageQueue = new();
        private readonly ConcurrentDictionary<string, QueuedMessage> _messageById = new();
        
        public MessageQueue(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        /// <summary>
        /// Enqueues a message for delayed processing
        /// </summary>
        /// <param name="message">The message to enqueue</param>
        /// <returns>A task that will complete when the message is processed</returns>
        public Task<JsonRpcResponse> EnqueueMessage(JsonRpcRequest message)
        {
            if (message == null)
            {
                throw new ArgumentNullException(nameof(message));
            }
            
            var queuedMessage = new QueuedMessage(message);
            _messageQueue.Enqueue(queuedMessage);
            
            if (message.Id != null)
            {
                string messageId = message.Id.ToString() ?? "null";
                _messageById[messageId] = queuedMessage;
            }
            
            _logger.Debug($"Enqueued message: {message.Method} with id {message.Id}");
            
            return queuedMessage.CompletionTask.Task;
        }
        
        /// <summary>
        /// Checks if a message with the given ID is queued
        /// </summary>
        /// <param name="id">The message ID to check</param>
        /// <returns>True if a message with the ID is queued, false otherwise</returns>
        public bool IsMessageQueued(object id)
        {
            if (id == null)
            {
                return false;
            }
            
            return _messageById.ContainsKey(id.ToString() ?? "null");
        }
        
        /// <summary>
        /// Gets the next message in the queue without removing it
        /// </summary>
        /// <returns>The next message in the queue, or null if the queue is empty</returns>
        public JsonRpcRequest? PeekNextMessage()
        {
            if (_messageQueue.TryPeek(out var queuedMessage))
            {
                return queuedMessage.Request;
            }
            
            return null;
        }
        
        /// <summary>
        /// Dequeues the next message in the queue
        /// </summary>
        /// <returns>The next message in the queue, or null if the queue is empty</returns>
        public QueuedMessage? DequeueNextMessage()
        {
            if (_messageQueue.TryDequeue(out var queuedMessage))
            {
                if (queuedMessage.Request.Id != null)
                {
                    string messageId = queuedMessage.Request.Id.ToString() ?? "null";
                    _messageById.TryRemove(messageId, out _);
                }
                
                _logger.Debug($"Dequeued message: {queuedMessage.Request.Method} with id {queuedMessage.Request.Id}");
                
                return queuedMessage;
            }
            
            return null;
        }
        
        /// <summary>
        /// Completes a message with the given response
        /// </summary>
        /// <param name="id">The ID of the message to complete</param>
        /// <param name="response">The response for the message</param>
        /// <returns>True if the message was completed, false otherwise</returns>
        public bool CompleteMessage(object id, JsonRpcResponse response)
        {
            if (id == null)
            {
                return false;
            }
            
            string messageId = id.ToString() ?? "null";
            if (_messageById.TryRemove(messageId, out var queuedMessage))
            {
                queuedMessage.CompletionTask.SetResult(response);
                _logger.Debug($"Completed message with id {id}");
                return true;
            }
            
            _logger.Error($"Failed to complete message with id {id}, message not found");
            return false;
        }
        
        /// <summary>
        /// Gets the count of queued messages
        /// </summary>
        public int Count => _messageQueue.Count;
        
        /// <summary>
        /// Checks if the queue is empty
        /// </summary>
        public bool IsEmpty => _messageQueue.IsEmpty;
    }
    
    /// <summary>
    /// Represents a message in the queue
    /// </summary>
    public class QueuedMessage
    {
        /// <summary>
        /// The original request
        /// </summary>
        public JsonRpcRequest Request { get; }
        
        /// <summary>
        /// Task source that will be completed when the message is processed
        /// </summary>
        public TaskCompletionSource<JsonRpcResponse> CompletionTask { get; }
        
        /// <summary>
        /// When the message was queued
        /// </summary>
        public DateTime QueueTime { get; }
        
        public QueuedMessage(JsonRpcRequest request)
        {
            Request = request;
            CompletionTask = new TaskCompletionSource<JsonRpcResponse>();
            QueueTime = DateTime.UtcNow;
        }
    }
} 