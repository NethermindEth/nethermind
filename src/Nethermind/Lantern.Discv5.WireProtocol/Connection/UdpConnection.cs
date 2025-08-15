using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Lantern.Discv5.WireProtocol.Logging.Exceptions;
using Lantern.Discv5.WireProtocol.Packet;
using Lantern.Discv5.WireProtocol.Utility;
using Microsoft.Extensions.Logging;
using IPEndPoint = System.Net.IPEndPoint;

namespace Lantern.Discv5.WireProtocol.Connection;

public sealed class UdpConnection(ConnectionOptions options, ILoggerFactory loggerFactory, IGracefulTaskRunner taskRunner) : IUdpConnection, IDisposable
{
    private readonly UdpClient _udpClient = new(new IPEndPoint(options.IpAddress ?? IPAddress.Any, options.UdpPort));
    private readonly ILogger<UdpConnection> _logger = loggerFactory.CreateLogger<UdpConnection>();
    private readonly Channel<UdpReceiveResult> _messageChannel = Channel.CreateUnbounded<UdpReceiveResult>();
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async Task SendAsync(byte[] data, IPEndPoint destination)
    {
        ValidatePacketSize(data);

        try
        {
            await _semaphore.WaitAsync();
            await _udpClient.SendAsync(data, data.Length, destination).ConfigureAwait(false);
        }
        catch (SocketException se)
        {
            _logger.LogError(se, "Error sending data");
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public async Task ListenAsync(CancellationToken token = default)
    {
        _logger.LogInformation("Starting ListenAsync");

        await taskRunner.RunWithGracefulCancellationAsync(async cancellationToken =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var returnedResult = await ReceiveAsync(cancellationToken).ConfigureAwait(false);
                    _messageChannel.Writer.TryWrite(returnedResult);

                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error occurred during an attempt to ReceiveAsync");
                }
            }
        }, "Listen", token);
    }

    public async IAsyncEnumerable<UdpReceiveResult> ReadMessagesAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var message in _messageChannel.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            yield return message;
        }
    }

    public void Close()
    {
        _logger.LogInformation("Closing UdpConnection");
        Dispose();
    }

    public void Dispose()
    {
        _logger.LogInformation("Disposing UdpConnection");
        Dispose(true);
    }

    private void Dispose(bool disposing)
    {
        if (!disposing)
            return;

        _semaphore.Dispose();
        _udpClient.Close();
        _udpClient.Dispose();
        _messageChannel.Writer.TryComplete();
    }

    public static void ValidatePacketSize(IReadOnlyCollection<byte> data)
    {
        switch (data.Count)
        {
            case < PacketConstants.MinPacketSize:
                throw new InvalidPacketException("Packet is too small");
            case > PacketConstants.MaxPacketSize:
                throw new InvalidPacketException("Packet is too large");
        }
    }

    private async Task<UdpReceiveResult> ReceiveAsync(CancellationToken token = default)
    {
        UdpReceiveResult receiveResult;
        try
        {
            receiveResult = await _udpClient.ReceiveAsync(token).ConfigureAwait(false);
            ValidatePacketSize(receiveResult.Buffer);
        }
        catch (InvalidPacketException ex)
        {
            _logger.LogWarning("{Message}, packet ignored. ", ex.Message);
            return await ReceiveAsync(token).ConfigureAwait(false);
        }

        _logger.LogDebug("Received packet from {Source}", receiveResult.RemoteEndPoint);
        return receiveResult;
    }
}
