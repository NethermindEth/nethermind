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
        
        // Fields for pause/resume functionality
        private volatile bool _processingPaused = false;
        private readonly SemaphoreSlim _pauseSemaphore = new SemaphoreSlim(1, 1);
        
        public MessageQueue(ILogManager logManager)
        {
            _logger = logManager?.GetClassLogger() ?? throw new ArgumentNullException(nameof(logManager));
        }
        
        /// <summary>
        /// Pauses message processing
        /// </summary>
        public void PauseProcessing()
        {
            if (!_processingPaused)
            {
                _processingPaused = true;
                _pauseSemaphore.Wait(0); // Acquire the semaphore to block processing
                _logger.Debug("Message processing paused");
            }
        }
        
        /// <summary>
        /// Resumes message processing
        /// </summary>
        public void ResumeProcessing()
        {
            if (_processingPaused)
            {
                _processingPaused = false;
                try
                {
                    _pauseSemaphore.Release(); // Release the semaphore to allow processing
                }
                catch (SemaphoreFullException)
                {
                    // Semaphore was already released
                }
                _logger.Debug("Message processing resumed");
            }
        }
        
        /// <summary>
        /// Checks if processing is currently paused
        /// </summary>
        public bool IsProcessingPaused => _processingPaused;
        
        /// <summary>
        /// Checks if the queue is empty
        /// </summary>
        public bool IsEmpty => _messageQueue.IsEmpty;

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
        /// Dequeues the next message from the queue
        /// </summary>
        /// <returns>The next message, or null if queue is empty or processing is paused</returns>
        public QueuedMessage? DequeueNextMessage()
        {
            // Check if processing is paused
            if (_processingPaused)
            {
                try
                {
                    // Wait on the semaphore to block processing
                    _pauseSemaphore.Wait(0);
                    _pauseSemaphore.Release(); // Release it immediately to not block other threads
                }
                catch (SemaphoreFullException)
                {
                    // Semaphore was already released
                }
                return null;
            }
            
            if (_messageQueue.TryDequeue(out var message))
            {
                if (message.Request.Id != null)
                {
                    string messageId = message.Request.Id.ToString() ?? "null";
                    _messageById.TryRemove(messageId, out _);
                }
                
                _logger.Debug($"Dequeued message: {message.Request.Method} with id {message.Request.Id}");
                return message;
            }
            
            return null;
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
            
            string messageId = id.ToString() ?? "null";
            return _messageById.ContainsKey(messageId);
        }
        
        /// <summary>
        /// Gets a message with the given ID from the queue
        /// </summary>
        /// <param name="id">The message ID to get</param>
        /// <returns>The queued message, or null if not found</returns>
        public QueuedMessage? GetMessage(object id)
        {
            if (id == null)
            {
                return null;
            }
            
            string messageId = id.ToString() ?? "null";
            if (_messageById.TryGetValue(messageId, out var message))
            {
                return message;
            }
            
            return null;
        }
        
        /// <summary>
        /// Clears all messages from the queue
        /// </summary>
        public void Clear()
        {
            while (_messageQueue.TryDequeue(out _)) { }
            _messageById.Clear();
            _logger.Debug("Message queue cleared");
        }
    }

    /// <summary>
    /// Represents a message queued for processing
    /// </summary>
    public class QueuedMessage
    {
        /// <summary>
        /// The original request
        /// </summary>
        public JsonRpcRequest Request { get; }
        
        /// <summary>
        /// Task source for completing the message
        /// </summary>
        public TaskCompletionSource<JsonRpcResponse> CompletionTask { get; }
        
        /// <summary>
        /// When the message was enqueued
        /// </summary>
        public DateTime EnqueuedTime { get; }
        
        public QueuedMessage(JsonRpcRequest request)
        {
            Request = request;
            CompletionTask = new TaskCompletionSource<JsonRpcResponse>();
            EnqueuedTime = DateTime.UtcNow;
        }
    }
} 