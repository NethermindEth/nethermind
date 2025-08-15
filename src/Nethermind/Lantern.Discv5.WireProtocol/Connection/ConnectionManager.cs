using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;

namespace Lantern.Discv5.WireProtocol.Connection;

public sealed class ConnectionManager(IPacketManager packetManager, IUdpConnection connection,
        ICancellationTokenSourceWrapper cts, IGracefulTaskRunner taskRunner, ILoggerFactory loggerFactory)
    : IConnectionManager
{
    private readonly ILogger<ConnectionManager> _logger = loggerFactory.CreateLogger<ConnectionManager>();
    private Task? _listenTask;
    private Task? _handleTask;

    public void InitAsync()
    {
        _logger.LogInformation("Starting ConnectionManagerAsync");

        _listenTask = taskRunner.RunWithGracefulCancellationAsync(connection.ListenAsync, "Listen", cts.GetToken());
        _handleTask = taskRunner.RunWithGracefulCancellationAsync(HandleIncomingPacketsAsync, "HandleIncomingPackets", cts.GetToken());
    }

    public async Task StopConnectionManagerAsync()
    {
        _logger.LogInformation("Stopping ConnectionManagerAsync");
        cts.Cancel();

        try
        {
            if (_listenTask != null && _handleTask != null)
            {
                await Task.WhenAll(_listenTask, _handleTask).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while waiting for tasks in StopConnectionManagerAsync: {Message}", ex.Message);
            throw;
        }

        if (cts.IsCancellationRequested())
        {
            _logger.LogInformation("ConnectionManagerAsync was canceled gracefully");
        }

        try
        {
            connection.Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while closing the connection: {Message}", ex.Message);
            throw;
        }
    }

    public async Task HandleIncomingPacketsAsync(CancellationToken token)
    {
        _logger.LogInformation("Starting HandleIncomingPacketsAsync");

        try
        {
            await foreach (var packet in connection.ReadMessagesAsync(token).ConfigureAwait(false))
            {
                try
                {
                    await packetManager.HandleReceivedPacket(packet).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to handle incoming packet: {Packet}. Error: {Message}", packet, ex.Message);
                }
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("HandleIncomingPacketsAsync has been cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in HandleIncomingPacketsAsync: {Message}", ex.Message);
            throw;
        }
    }
}
