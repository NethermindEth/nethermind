// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Nethermind.Core.Test.IO;
using NUnit.Framework;

namespace Nethermind.Sockets.Test;

[TestFixture]
[Parallelizable(ParallelScope.All)]
public class IpcSocketMessageStreamTests
{
    #region Helpers

    /// <summary>
    /// Creates a connected Unix Domain Socket pair for testing.
    /// Returns (listenerAcceptedSocket, clientSocket, cleanup).
    /// </summary>
    private static async Task<(Socket Server, Socket Client, IDisposable Cleanup)> CreateSocketPairAsync()
    {
        TempPath tmpPath = TempPath.GetTempFile();
        UnixDomainSocketEndPoint endPoint = new(tmpPath.Path);
        Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(endPoint);
        listener.Listen(1);

        Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await client.ConnectAsync(endPoint);
        Socket server = await listener.AcceptAsync();

        return (server, client, new Cleanup(listener, tmpPath));
    }

    private sealed class Cleanup(Socket listener, TempPath tmpPath) : IDisposable
    {
        public void Dispose()
        {
            listener.Dispose();
            tmpPath.Dispose();
        }
    }

    /// <summary>
    /// Reads a complete message by calling ReceiveAsync in a loop until EndOfMessage is true.
    /// Uses the given bufferSize for each call.
    /// </summary>
    private static async Task<byte[]> ReadOneMessageAsync(IpcSocketMessageStream stream, int bufferSize)
    {
        List<byte> result = [];
        byte[] buffer = new byte[bufferSize];

        while (true)
        {
            ReceiveResult rr = await stream.ReceiveAsync(buffer);
            if (rr.IsNull || rr.Closed) break;

            for (int i = 0; i < rr.Read; i++)
                result.Add(buffer[i]);

            if (rr.EndOfMessage)
                break;
        }

        return result.ToArray();
    }

    /// <summary>
    /// Reads all messages until the connection closes.
    /// Uses the given bufferSize for each ReceiveAsync call.
    /// </summary>
    private static async Task<List<byte[]>> ReadAllMessagesAsync(IpcSocketMessageStream stream, int bufferSize)
    {
        List<byte[]> messages = [];
        List<byte> current = [];
        byte[] buffer = new byte[bufferSize];

        while (true)
        {
            ReceiveResult rr = await stream.ReceiveAsync(buffer);
            if (rr.IsNull || rr.Closed)
            {
                if (current.Count > 0)
                    messages.Add(current.ToArray());
                break;
            }

            for (int i = 0; i < rr.Read; i++)
                current.Add(buffer[i]);

            if (rr.EndOfMessage)
            {
                messages.Add(current.ToArray());
                current = [];
            }
        }

        return messages;
    }

    /// <summary>
    /// Reads all messages using SocketClient-style buffer management (ArraySegment with growing offset).
    /// This exercises the code path where buffer.Offset != 0.
    /// Optionally shrinks buffer after processing a message (like SocketClient does).
    /// </summary>
    private static async Task<List<byte[]>> ReadAllMessagesSocketClientStyleAsync(
        IpcSocketMessageStream stream, int bufferSize, bool shrinkAfterMessage = false)
    {
        List<byte[]> messages = [];
        byte[] buffer = new byte[bufferSize];
        int currentMessageLength = 0;

        while (true)
        {
            ArraySegment<byte> segment = new(buffer, currentMessageLength, buffer.Length - currentMessageLength);
            ReceiveResult rr = await stream.ReceiveAsync(segment);

            if (rr.IsNull || rr.Closed) break;

            currentMessageLength += rr.Read;

            if (rr.EndOfMessage)
            {
                byte[] msg = new byte[currentMessageLength];
                Buffer.BlockCopy(buffer, 0, msg, 0, currentMessageLength);
                messages.Add(msg);
                currentMessageLength = 0;

                // Simulate SocketClient's buffer shrink after large messages
                if (shrinkAfterMessage && buffer.Length > bufferSize)
                {
                    buffer = new byte[bufferSize];
                }
            }
            else if (buffer.Length - currentMessageLength < bufferSize / 2)
            {
                // Grow
                byte[] newBuffer = new byte[buffer.Length * 4];
                Buffer.BlockCopy(buffer, 0, newBuffer, 0, currentMessageLength);
                buffer = newBuffer;
            }
        }

        return messages;
    }

    #endregion

    #region Basic functionality

    [Test]
    public async Task Basic_newline_delimited_message_roundtrip()
    {
        TestContext.Out.WriteLine("DIAG: test starting");
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;
        TestContext.Out.WriteLine("DIAG: socket pair created");

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        byte[] payload = "hello world"u8.ToArray();
        await sendStream.WriteAsync(payload);
        await sendStream.WriteEndOfMessageAsync();
        TestContext.Out.WriteLine("DIAG: data sent");

        TestContext.Out.WriteLine("DIAG: about to read");
        Task<byte[]> readTask = ReadOneMessageAsync(recvStream, 1024);
        Task completed = await Task.WhenAny(readTask, Task.Delay(5000));
        if (completed != readTask)
        {
            TestContext.Out.WriteLine("DIAG: READ TIMED OUT AFTER 5 SECONDS");
            throw new TimeoutException("ReadOneMessageAsync timed out after 5 seconds");
        }
        byte[] received = readTask.Result;
        TestContext.Out.WriteLine($"DIAG: received {received.Length} bytes");
        received.Should().Equal(payload);
    }

    [TestCase(5)]
    [TestCase(10)]
    [TestCase(100)]
    public async Task Multiple_newline_delimited_messages(int count)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        List<byte[]> expected = [];
        for (int i = 0; i < count; i++)
        {
            byte[] msg = Encoding.UTF8.GetBytes($"message_{i}_{new string((char)('A' + i % 26), 50)}");
            expected.Add(msg);
            await sendStream.WriteAsync(msg);
            await sendStream.WriteEndOfMessageAsync();
        }

        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesAsync(recvStream, 1024);
        received.Should().HaveCount(count);
        for (int i = 0; i < count; i++)
        {
            received[i].Should().Equal(expected[i], $"message {i} should match");
        }
    }

    #endregion

    #region Bug 1: PooledBuffer.Append prepends instead of appending when partially drained

    /// <summary>
    /// This test exercises the bug in PooledBuffer.Append where the "optimization"
    /// branch (when _length > 0 && _offset >= source.Length) prepends data instead
    /// of appending it, causing message data corruption.
    ///
    /// The scenario: send two messages back-to-back where the second message's data
    /// partially fills the overflow buffer, then gets reordered when a new append occurs
    /// after a partial drain.
    ///
    /// To trigger this, we need:
    /// 1. Overflow contains more data than the read buffer can drain in one CopyTo call
    /// 2. A message boundary is found within the partially-drained portion
    /// 3. Post-boundary bytes are Append'd while undrained data still exists in overflow
    /// </summary>
    [Test]
    public async Task Bug_Append_prepends_instead_of_appending_when_overflow_partially_drained()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // Send: "AAA\nBBBBBBBBBB\nCC\n" all at once.
        // With a small receive buffer (e.g., 5 bytes), the first ReceiveAsync processes "AAA\n",
        // leaving "BBBBBBBBBB\nCC\n" in overflow.
        // Next call with 5-byte buffer: CopyTo drains 5 bytes from overflow("BBBBB"),
        // leaving "BBBBB\nCC\n" still in overflow.
        // Since no boundary in "BBBBB", it reads more from socket (nothing left in socket).
        // Returns Read=5, EndOfMessage=false.
        // Next call: CopyTo drains 5 from overflow "BBBBB", but "\nCC\n" remains.
        // Actually... let me use messages that trigger partial drain with boundary.

        // Better approach: send data that creates overflow with boundary after partial drain
        string msg1 = "A";       // short msg
        string msg2 = "BCDEFGH"; // 7 bytes
        string msg3 = "XY";      // 2 bytes

        // Write all at once: "A\nBCDEFGH\nXY\n"
        byte[] all = Encoding.UTF8.GetBytes(msg1 + "\n" + msg2 + "\n" + msg3 + "\n");
        await sendStream.WriteAsync(all);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        // Use tiny buffer (5 bytes) to force partial overflow drains
        List<byte[]> messages = await ReadAllMessagesAsync(recvStream, 5);

        messages.Should().HaveCount(3);
        messages[0].Should().Equal(Encoding.UTF8.GetBytes(msg1), "msg1");
        messages[1].Should().Equal(Encoding.UTF8.GetBytes(msg2), "msg2");
        messages[2].Should().Equal(Encoding.UTF8.GetBytes(msg3), "msg3");
    }

    /// <summary>
    /// A more direct test: force a scenario where overflow has 12+ bytes,
    /// read buffer is just 5, causing the Append prepend bug.
    ///
    /// Burst: "A\nBCDEFGHIJKLMN\nZ\n"
    ///   msg1 = "A" (1 byte)
    ///   msg2 = "BCDEFGHIJKLMN" (13 bytes)
    ///   msg3 = "Z" (1 byte)
    ///
    /// With buffer=5:
    /// Call 1: recv "A\nBCD" (5 bytes), find \n at 1 → msg1 done. Overflow="BCD" (buffer[2..5])
    ///         But socket still has "EFGHIJKLMN\nZ\n"
    /// Call 2: CopyTo drains "BCD" (3≤5). read=3. No boundary in "BCD".
    ///         Socket read → gets up to 2 more bytes "EF". read=5. "BCDEF" no boundary.
    ///         Returns Read=5, EndOfMessage=false.
    /// ...this gets complicated. Let's try a cleaner approach.
    /// </summary>
    [Test]
    public async Task Bug_overflow_data_order_corruption_with_small_buffer()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // Send a burst containing multiple short messages: each < 5 bytes + delimiter
        // This ensures overflow accumulates multiple messages and partial drains happen.
        string msg1 = "AA";
        string msg2 = "BB";
        string msg3 = "CC";
        string msg4 = "DD";
        string msg5 = "EE";

        // "AA\nBB\nCC\nDD\nEE\n" = 15 bytes
        byte[] burst = Encoding.UTF8.GetBytes($"{msg1}\n{msg2}\n{msg3}\n{msg4}\n{msg5}\n");
        await sendStream.WriteAsync(burst);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        // Buffer of 4 bytes: overflow will accumulate across calls
        List<byte[]> messages = await ReadAllMessagesAsync(recvStream, 4);

        messages.Should().HaveCount(5);
        messages[0].Should().Equal("AA"u8.ToArray());
        messages[1].Should().Equal("BB"u8.ToArray());
        messages[2].Should().Equal("CC"u8.ToArray());
        messages[3].Should().Equal("DD"u8.ToArray());
        messages[4].Should().Equal("EE"u8.ToArray());
    }

    /// <summary>
    /// Systematically test with various buffer sizes that data integrity is preserved.
    /// Smaller buffers are more likely to trigger the overflow partial-drain + Append issue.
    /// </summary>
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(7)]
    [TestCase(10)]
    [TestCase(15)]
    public async Task Message_integrity_with_various_buffer_sizes(int bufferSize)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // Create messages of varying sizes to exercise different overflow scenarios
        List<byte[]> expected = [];
        byte[] allData;
        {
            StringBuilder sb = new();
            for (int i = 1; i <= 20; i++)
            {
                string msg = new string((char)('A' + i % 26), i);
                expected.Add(Encoding.UTF8.GetBytes(msg));
                sb.Append(msg);
                sb.Append('\n');
            }
            allData = Encoding.UTF8.GetBytes(sb.ToString());
        }

        // Send everything in one burst
        await sendStream.WriteAsync(allData);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesAsync(recvStream, bufferSize);

        received.Should().HaveCount(expected.Count, "should receive all messages");
        for (int i = 0; i < expected.Count; i++)
        {
            received[i].Should().Equal(expected[i], $"message {i} content should match");
        }
    }

    #endregion

    #region Bug: Buffer shrink causes overflow > buffer, triggering Append prepend

    /// <summary>
    /// This test replicates the exact SocketClient buffer-shrink scenario:
    /// 1. A large message (>2*standardBufferLength) causes buffer to grow
    /// 2. After processing, buffer shrinks back to standardBufferLength
    /// 3. The overflow from the large-message boundary has more bytes than the shrunk buffer
    /// 4. Next CopyTo partially drains → Append is called with _length > 0
    /// 5. The Append "optimization" prepends instead of appending → DATA CORRUPTION
    ///
    /// We simulate this by:
    /// - Using a small "standard" buffer (e.g., 8 bytes)
    /// - Sending msg1 (large, >16 to trigger growth) + "\n" + msg2 + msg3 (total > 8 bytes)
    /// - After msg1, buffer shrinks to 8, but overflow has msg2+msg3 data (>8 bytes)
    /// - CopyTo for msg2 partially drains overflow
    /// - If msg2 boundary is found in the drained portion, post-boundary bytes
    ///   get Append'd while _length > 0 → BUG: prepended instead of appended
    /// </summary>
    [Test]
    public async Task Bug_buffer_shrink_causes_overflow_append_prepend()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        int smallBuf = 8;

        // msg1: large enough that buffer grows beyond 2*smallBuf=16
        string msg1 = new('L', 20);                         // 20 bytes
        // msg2 + msg3: together > smallBuf=8 to force partial overflow drain after shrink
        string msg2 = "ABCD";                                // 4 bytes
        string msg3 = "EFGHIJ";                              // 6 bytes
        // Total overflow after msg1 boundary: "ABCD\nEFGHIJ\n" = 12 bytes > 8

        byte[] data = Encoding.UTF8.GetBytes(msg1 + "\n" + msg2 + "\n" + msg3 + "\n");
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, smallBuf, shrinkAfterMessage: true);

        received.Should().HaveCount(3);
        Encoding.UTF8.GetString(received[0]).Should().Be(msg1, "msg1 should be intact");
        Encoding.UTF8.GetString(received[1]).Should().Be(msg2, "msg2 should be intact (BUG: may be corrupted by prepend)");
        Encoding.UTF8.GetString(received[2]).Should().Be(msg3, "msg3 should be intact (BUG: may have wrong data)");
    }

    /// <summary>
    /// Same concept but with larger data to make the bug more likely to manifest
    /// despite socket buffering variations.
    /// </summary>
    [TestCase(8)]
    [TestCase(16)]
    [TestCase(32)]
    public async Task Bug_buffer_shrink_data_corruption_various_sizes(int smallBuf)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // msg1: large enough to grow buffer beyond 2*smallBuf
        string msg1 = new('L', smallBuf * 3);

        // Several small messages that together exceed smallBuf in overflow
        List<string> expectedMessages = [msg1];
        StringBuilder sb = new();
        sb.Append(msg1); sb.Append('\n');

        int totalSmallSize = 0;
        for (int i = 0; totalSmallSize < smallBuf * 2; i++)
        {
            string msg = new string((char)('A' + i), (i % 5) + 2);
            expectedMessages.Add(msg);
            sb.Append(msg); sb.Append('\n');
            totalSmallSize += msg.Length + 1;
        }

        byte[] data = Encoding.UTF8.GetBytes(sb.ToString());
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, smallBuf, shrinkAfterMessage: true);

        received.Should().HaveCount(expectedMessages.Count, $"smallBuf={smallBuf}");
        for (int i = 0; i < expectedMessages.Count; i++)
        {
            Encoding.UTF8.GetString(received[i]).Should().Be(expectedMessages[i],
                $"message {i} with smallBuf={smallBuf}");
        }
    }

    /// <summary>
    /// Direct test: force overflow to be larger than buffer by:
    /// 1. Sending all data at once
    /// 2. Waiting for it to arrive in the kernel buffer
    /// 3. Using a large first read to slurp everything (filling overflow)
    /// 4. Then switching to a tiny buffer (simulating buffer shrink)
    ///
    /// This forces CopyTo to partially drain the overflow.
    /// If the Append prepend bug exists, msg2/msg3/msg4 content will be corrupted.
    /// </summary>
    [Test]
    public async Task Direct_overflow_larger_than_buffer_causes_data_corruption()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // Messages: short enough that they all fit in one large recv
        string msg1 = "M1";       // 2 bytes
        string msg2 = "ABCDE";    // 5 bytes
        string msg3 = "FGHIJK";   // 6 bytes
        string msg4 = "LM";       // 2 bytes

        // "M1\nABCDE\nFGHIJK\nLM\n" = 21 bytes
        byte[] data = Encoding.UTF8.GetBytes($"{msg1}\n{msg2}\n{msg3}\n{msg4}\n");
        await sendStream.WriteAsync(data);
        // Ensure all data is flushed and in the kernel buffer
        await Task.Delay(50);

        // Read with a large buffer first — this should get ALL the data
        byte[] largeBuf = new byte[1024];
        ReceiveResult rr = await recvStream.ReceiveAsync(largeBuf);
        // Should find msg1 boundary and put the rest in overflow
        rr.EndOfMessage.Should().BeTrue("should find msg1 boundary");
        byte[] firstMsg = largeBuf[..rr.Read];
        Encoding.UTF8.GetString(firstMsg).Should().Be(msg1, "msg1 content");

        // Now overflow should contain: "ABCDE\nFGHIJK\nLM\n" (18 bytes)
        // Use a buffer of 5 — smaller than the overflow!
        // CopyTo will partially drain, then FindMessageEnd may find \n in drained part,
        // then Append stores post-\n bytes while _length > 0.
        int tinyBuf = 5;
        List<byte[]> remaining = [];
        List<byte> current = [];
        byte[] buf = new byte[tinyBuf];

        while (true)
        {
            rr = await recvStream.ReceiveAsync(buf);
            if (rr.IsNull || rr.Closed)
            {
                if (current.Count > 0) remaining.Add(current.ToArray());
                break;
            }

            for (int i = 0; i < rr.Read; i++) current.Add(buf[i]);

            if (rr.EndOfMessage)
            {
                remaining.Add(current.ToArray());
                current = [];
            }
        }

        remaining.Should().HaveCount(3, "should have msg2, msg3, msg4");
        Encoding.UTF8.GetString(remaining[0]).Should().Be(msg2, "msg2 (BUG: may be corrupted by Append prepend)");
        Encoding.UTF8.GetString(remaining[1]).Should().Be(msg3, "msg3 (BUG: may have wrong data order)");
        Encoding.UTF8.GetString(remaining[2]).Should().Be(msg4, "msg4");
    }

    #endregion

    #region Bug 2: SocketClient-style offset usage corruption

    /// <summary>
    /// When SocketClient calls ReceiveAsync with buffer.Offset != 0 (partial message accumulation),
    /// the overflow.CopyTo writes data starting at buffer.Offset in the underlying array.
    /// FullBufferSpan then includes [0..Offset] (old data from previous reads) plus [Offset..Offset+read].
    ///
    /// Test that messages are correctly preserved with the SocketClient-style accumulation pattern.
    /// </summary>
    [Test]
    public async Task SocketClient_style_offset_accumulation_preserves_messages()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string msg1 = "Hello, World!";
        string msg2 = "Second message with more data here";
        string msg3 = "Third";

        byte[] data = Encoding.UTF8.GetBytes(msg1 + "\n" + msg2 + "\n" + msg3 + "\n");
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 16);

        received.Should().HaveCount(3);
        Encoding.UTF8.GetString(received[0]).Should().Be(msg1);
        Encoding.UTF8.GetString(received[1]).Should().Be(msg2);
        Encoding.UTF8.GetString(received[2]).Should().Be(msg3);
    }

    /// <summary>
    /// Test that when SocketClient reads a partial message (no boundary in current chunk),
    /// then the next ReceiveAsync call with non-zero Offset correctly handles overflow + socket reads.
    /// </summary>
    [TestCase(10)]
    [TestCase(32)]
    [TestCase(64)]
    public async Task SocketClient_style_partial_reads_then_boundary(int bufferSize)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // A message longer than bufferSize to force partial reads
        string longPayload = new('X', bufferSize * 3);
        string msg1 = longPayload;
        string msg2 = "short";

        byte[] data = Encoding.UTF8.GetBytes(msg1 + "\n" + msg2 + "\n");
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, bufferSize);

        received.Should().HaveCount(2);
        Encoding.UTF8.GetString(received[0]).Should().Be(msg1);
        Encoding.UTF8.GetString(received[1]).Should().Be(msg2);
    }

    #endregion

    #region Bug 3: JSON boundary detection across partial reads with non-zero offset

    /// <summary>
    /// When messages don't end with newline but are complete JSON objects,
    /// the JSON boundary detector scans FullBufferSpan (which starts at index 0).
    /// With partial reads (non-zero offset), this must correctly detect the JSON end.
    /// </summary>
    [TestCase(10)]
    [TestCase(32)]
    [TestCase(64)]
    public async Task Json_boundary_detection_with_partial_reads(int bufferSize)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string json1 = "{\"id\":1,\"method\":\"test\"}";
        string json2 = "{\"id\":2,\"method\":\"test2\"}";

        // Send without delimiter — relies on JSON parsing
        byte[] data = Encoding.UTF8.GetBytes(json1 + json2);
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, bufferSize);

        received.Should().HaveCount(2);
        Encoding.UTF8.GetString(received[0]).Should().Be(json1);
        Encoding.UTF8.GetString(received[1]).Should().Be(json2);
    }

    /// <summary>
    /// Large JSON messages that span many buffer reads, without newline delimiters.
    /// Tests incremental JSON parsing across multiple ReceiveAsync calls.
    /// </summary>
    [Test]
    public async Task Large_json_without_delimiter_spans_multiple_reads()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // ~10KB JSON payload
        string largeValue = new('Z', 10_000);
        string json = $"{{\"jsonrpc\":\"2.0\",\"id\":1,\"params\":[\"{largeValue}\"]}}";

        byte[] data = Encoding.UTF8.GetBytes(json);
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        // Use SocketClient-style with small buffer to force many partial reads
        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 512);

        received.Should().HaveCount(1);
        Encoding.UTF8.GetString(received[0]).Should().Be(json);
    }

    #endregion

    #region Concurrent message bursts

    /// <summary>
    /// Test that rapidly sending many messages in bursts doesn't lose or corrupt data.
    /// </summary>
    [TestCase(3)]
    [TestCase(7)]
    [TestCase(16)]
    [TestCase(128)]
    public async Task Burst_send_many_messages_preserves_order_and_content(int bufferSize)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // Send 50 messages of varying lengths in a single burst
        List<byte[]> expected = [];
        StringBuilder sb = new();
        for (int i = 0; i < 50; i++)
        {
            // Varying lengths: some shorter than buffer, some equal, some longer
            int msgLen = (i * 7 + 3) % 30 + 1;
            byte[] msgBytes = new byte[msgLen];
            for (int j = 0; j < msgLen; j++)
                msgBytes[j] = (byte)(32 + ((i * 7 + j) % 94)); // printable ASCII, avoiding \n
            // Make sure no newlines in the payload
            for (int j = 0; j < msgLen; j++)
                if (msgBytes[j] == (byte)'\n') msgBytes[j] = (byte)'_';

            expected.Add(msgBytes);
            sb.Append(Encoding.ASCII.GetString(msgBytes));
            sb.Append('\n');
        }

        byte[] allData = Encoding.ASCII.GetBytes(sb.ToString());
        await sendStream.WriteAsync(allData);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesAsync(recvStream, bufferSize);

        received.Should().HaveCount(expected.Count, $"with bufferSize={bufferSize}");
        for (int i = 0; i < expected.Count; i++)
        {
            received[i].Should().Equal(expected[i], $"message {i} with bufferSize={bufferSize}");
        }
    }

    #endregion

    #region Incremental send

    /// <summary>
    /// Send data byte-by-byte to test that the stream correctly assembles messages
    /// even when data arrives one byte at a time.
    /// </summary>
    [Test]
    public async Task Byte_by_byte_send_assembles_correctly()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string msg1 = "Hello";
        string msg2 = "World";
        byte[] data = Encoding.UTF8.GetBytes(msg1 + "\n" + msg2 + "\n");

        // Send byte by byte with small delays
        for (int i = 0; i < data.Length; i++)
        {
            await sendStream.WriteAsync(new[] { data[i] });
            if (i % 3 == 0) await Task.Delay(1);
        }
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        received.Should().HaveCount(2);
        Encoding.UTF8.GetString(received[0]).Should().Be(msg1);
        Encoding.UTF8.GetString(received[1]).Should().Be(msg2);
    }

    #endregion

    #region Edge cases

    /// <summary>
    /// Test empty messages (consecutive delimiters).
    /// </summary>
    [Test]
    public async Task Empty_messages_between_delimiters()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // "\n\n\n" = three empty messages? Or is the delimiter consumed and empty data not yielded?
        // Let's see: "A\n\nB\n" - msg1="A", msg2="", msg3="B"
        byte[] data = Encoding.UTF8.GetBytes("A\n\nB\n");
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesAsync(recvStream, 1024);

        // At minimum, A and B should be present and intact
        List<string> strings = received.Select(b => Encoding.UTF8.GetString(b)).Where(s => s.Length > 0).ToList();
        strings.Should().Contain("A");
        strings.Should().Contain("B");
    }

    /// <summary>
    /// Message exactly equal to buffer size.
    /// </summary>
    [TestCase(5)]
    [TestCase(10)]
    [TestCase(100)]
    public async Task Message_exactly_buffer_size(int bufferSize)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string msg = new('X', bufferSize);
        byte[] data = Encoding.UTF8.GetBytes(msg + "\n");
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        byte[] received = await ReadOneMessageAsync(recvStream, bufferSize);
        Encoding.UTF8.GetString(received).Should().Be(msg);
    }

    /// <summary>
    /// Message one byte larger than buffer size.
    /// </summary>
    [TestCase(5)]
    [TestCase(10)]
    public async Task Message_one_byte_larger_than_buffer(int bufferSize)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string msg = new('Y', bufferSize + 1);
        byte[] data = Encoding.UTF8.GetBytes(msg + "\n");
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, bufferSize);
        received.Should().HaveCount(1);
        Encoding.UTF8.GetString(received[0]).Should().Be(msg);
    }

    #endregion

    #region PooledBuffer.Append correctness — isolated scenario

    /// <summary>
    /// This test precisely constructs the scenario where the PooledBuffer.Append
    /// optimization bug manifests:
    ///
    /// 1. Send "XX\n" + "YYYYYY\n" + "Z\n" in one burst (total: 13 bytes)
    /// 2. Receive with buffer size 3
    ///
    /// Expected flow (with correct Append):
    ///   Call 1: recv 3 bytes "XX\n" → find \n at 2 → msg1="XX". overflow="" (consumed=3=read)
    ///   Call 2: recv 3 bytes "YYY" → no \n → Read=3, no EOM
    ///   Call 3: recv 3 bytes "YYY" → no \n → Read=3, no EOM (accumulated 6)
    ///   Call 4: recv 2 bytes "\nZ" → find \n at 0 → msg2="YYYYYY". overflow="Z"
    ///   Call 5: CopyTo drains "Z" (1 byte). No \n. Socket recv=1 "\n" → msg3="Z"
    ///
    /// With SocketClient-style (offset-based):
    ///   Call 1: seg=[0..3], recv "XX\n" → \n at 2 → msg1="XX". overflow=empty
    ///   Call 2: seg=[0..3], recv "YYY" → no \n → Read=3, no EOM. offset=3
    ///   Call 3: seg=[3..6], recv "YYY" → check full [0..6]="YYYYYY" no \n → Read=3, no EOM. offset=6
    ///   ... continues until \n found
    /// </summary>
    [Test]
    public async Task Precise_overflow_append_order_scenario()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // Scenario: messages of specific sizes to trigger partial overflow drain
        string msg1 = "XX";
        string msg2 = "YYYYYY";
        string msg3 = "Z";

        byte[] burst = Encoding.UTF8.GetBytes($"{msg1}\n{msg2}\n{msg3}\n");
        await sendStream.WriteAsync(burst);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesAsync(recvStream, 3);

        received.Should().HaveCount(3);
        Encoding.UTF8.GetString(received[0]).Should().Be(msg1);
        Encoding.UTF8.GetString(received[1]).Should().Be(msg2);
        Encoding.UTF8.GetString(received[2]).Should().Be(msg3);
    }

    /// <summary>
    /// Force the specific Append bug path: overflow must have _offset > 0 and _length > 0
    /// when Append is called with source.Length <= _offset.
    ///
    /// This happens when:
    /// - A large burst fills overflow with many bytes
    /// - CopyTo partially drains it (leaving _offset > 0, _length > 0)
    /// - A delimiter is found in the drained portion
    /// - Post-delimiter bytes are Append'd back to overflow
    ///
    /// Concrete: send "A\n" followed by enough data to create large overflow,
    /// then use buffer size 1 smaller than overflow to force partial drain.
    /// </summary>
    [TestCase(2)]
    [TestCase(3)]
    [TestCase(4)]
    [TestCase(5)]
    [TestCase(6)]
    public async Task Append_bug_with_buffer_smaller_than_overflow(int bufferSize)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // Build data that will create overflow > bufferSize after first boundary
        // First message short, then many short messages packed tightly
        List<string> expectedMessages = [];
        StringBuilder sb = new();

        // Add messages with lengths 1, 2, 3, ..., 10
        for (int len = 1; len <= 10; len++)
        {
            string msg = new string((char)('A' + len - 1), len);
            expectedMessages.Add(msg);
            sb.Append(msg);
            sb.Append('\n');
        }

        byte[] allData = Encoding.UTF8.GetBytes(sb.ToString());
        await sendStream.WriteAsync(allData);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesAsync(recvStream, bufferSize);

        received.Should().HaveCount(expectedMessages.Count, $"bufferSize={bufferSize}");
        for (int i = 0; i < expectedMessages.Count; i++)
        {
            Encoding.UTF8.GetString(received[i]).Should().Be(expectedMessages[i],
                $"message {i} ('{expectedMessages[i]}') with bufferSize={bufferSize}");
        }
    }

    #endregion

    #region Bug: JSON detection with isFinalBlock:false — number at buffer end

    /// <summary>
    /// When using JSON boundary detection (no newlines), if the last value in the JSON
    /// is a number and the number ends at the buffer boundary, isFinalBlock:false may
    /// prevent the reader from consuming the number (it doesn't know if more digits follow).
    /// This means the EndObject/EndArray following the number is never reached in that call,
    /// potentially causing the message to never be detected as complete.
    ///
    /// With SocketClient-style accumulation: the number sits at the end of the received chunk,
    /// reader can't consume it, state is saved. Next chunk brings the closing }, which allows
    /// the reader to continue. This SHOULD work... but does it?
    ///
    /// This test sends `{"id":1}` byte-by-byte to control exactly what the reader sees.
    /// </summary>
    [Test]
    public async Task Json_number_at_buffer_end_detected_correctly()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // Send JSON without newline, byte-by-byte, so the reader encounters
        // the number `1` at the buffer boundary before seeing `}`
        string json = "{\"id\":1}";
        byte[] data = Encoding.UTF8.GetBytes(json);

        for (int i = 0; i < data.Length; i++)
        {
            await sendStream.WriteAsync(new[] { data[i] });
            await Task.Delay(5); // ensure each byte is a separate TCP segment
        }
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        // Use SocketClient-style with a large enough buffer
        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        received.Should().HaveCount(1, "JSON message should be detected as complete");
        Encoding.UTF8.GetString(received[0]).Should().Be(json);
    }

    /// <summary>
    /// Test: JSON with number value that arrives in a chunk that ends right after the number.
    /// {"id":123} sent as two chunks: {"id":123 and }
    /// The reader sees {"id":123 — the number 123 is at the end with no following character.
    /// With isFinalBlock:false, it may NOT consume 123.
    /// </summary>
    [Test]
    public async Task Json_number_at_chunk_boundary_across_two_reads()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string json = "{\"id\":123}";
        byte[] data = Encoding.UTF8.GetBytes(json);

        // Send {"id":123 first, then } after a delay
        int splitAt = json.IndexOf('}');
        await sendStream.WriteAsync(data.AsMemory(0, splitAt));
        await Task.Delay(50); // ensure separate TCP segments
        await sendStream.WriteAsync(data.AsMemory(splitAt));
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        received.Should().HaveCount(1, "JSON should be detected");
        Encoding.UTF8.GetString(received[0]).Should().Be(json);
    }

    /// <summary>
    /// Test: two JSON messages without newlines, second value ends with a number.
    /// {"a":1}{"b":99} — the reader must detect the first JSON, then the second.
    /// The second JSON's number 99 might sit at the buffer end.
    /// </summary>
    [TestCase(8)]   // Split inside second JSON
    [TestCase(10)]  // Split at "99"
    [TestCase(14)]  // Everything in one read
    public async Task Two_json_no_newline_second_ends_with_number(int bufferSize)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string json1 = "{\"a\":1}";
        string json2 = "{\"b\":99}";

        byte[] data = Encoding.UTF8.GetBytes(json1 + json2);
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, bufferSize);

        received.Should().HaveCount(2, "Two JSON messages should be detected");
        Encoding.UTF8.GetString(received[0]).Should().Be(json1, "first JSON");
        Encoding.UTF8.GetString(received[1]).Should().Be(json2, "second JSON");
    }

    /// <summary>
    /// Test: JSON with true/false/null at value position (these are known to be
    /// problematic with isFinalBlock:false when at buffer end).
    /// {"ok":true} sent as {"ok":true and } separately.
    /// </summary>
    [TestCase("{\"ok\":true}")]
    [TestCase("{\"ok\":false}")]
    [TestCase("{\"ok\":null}")]
    public async Task Json_boolean_null_at_chunk_boundary(string json)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        byte[] data = Encoding.UTF8.GetBytes(json);

        // Split right before the }
        int splitAt = json.LastIndexOf('}');
        await sendStream.WriteAsync(data.AsMemory(0, splitAt));
        await Task.Delay(50);
        await sendStream.WriteAsync(data.AsMemory(splitAt));
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        received.Should().HaveCount(1, $"JSON '{json}' should be detected");
        Encoding.UTF8.GetString(received[0]).Should().Be(json);
    }

    /// <summary>
    /// Critical test: JSON without newline sent, then connection closed.
    /// This is the "last message" case. With isFinalBlock:false, the reader
    /// might not detect the JSON as complete if the closing } is the last byte
    /// in the buffer with no following bytes.
    /// After the socket returns 0 (closed), the message may be lost.
    /// </summary>
    [Test]
    public async Task Last_json_message_before_disconnect_without_newline()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":true}";
        byte[] data = Encoding.UTF8.GetBytes(json);

        await sendStream.WriteAsync(data);
        await Task.Delay(50);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        received.Should().HaveCount(1, "Last JSON before disconnect should not be lost");
        Encoding.UTF8.GetString(received[0]).Should().Be(json);
    }

    /// <summary>
    /// Test: Multiple JSON messages without newlines, followed by close.
    /// The LAST message has no trailing data — tests if it's detected before close.
    /// </summary>
    [Test]
    public async Task Multiple_json_no_newline_last_before_close()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string json1 = "{\"id\":1}";
        string json2 = "{\"id\":2}";
        string json3 = "{\"id\":3}";

        // Send all three concatenated, no newlines
        byte[] data = Encoding.UTF8.GetBytes(json1 + json2 + json3);
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        received.Should().HaveCount(3, "All three JSON messages should be detected");
        Encoding.UTF8.GetString(received[0]).Should().Be(json1);
        Encoding.UTF8.GetString(received[1]).Should().Be(json2);
        Encoding.UTF8.GetString(received[2]).Should().Be(json3);
    }

    /// <summary>
    /// Test: JSON with leading whitespace before the { — tests GetStartingOffset behavior.
    /// </summary>
    [Test]
    public async Task Json_with_leading_whitespace_detected()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        // Leading spaces + tabs before JSON
        string jsonWithWs = "   \t  {\"id\":1}";
        byte[] data = Encoding.UTF8.GetBytes(jsonWithWs);
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        received.Should().HaveCount(1, "JSON with leading whitespace should be detected");
        // The contentLength includes the whitespace since startOffset is part of the calculation
        // We just verify the message was detected, content may include leading whitespace
        string receivedStr = Encoding.UTF8.GetString(received[0]);
        receivedStr.Should().Contain("{\"id\":1}");
    }

    #endregion

    #region Bug: Newline search spans across JSON message boundaries

    /// <summary>
    /// BUG: FindMessageEnd scans for \n across the ENTIRE available data via IndexOf(Delimiter).
    /// A complete JSON object without trailing \n, followed by a newline-delimited message,
    /// causes the \n from the SECOND message to act as the delimiter for the combined blob.
    ///
    /// Root cause: The newline fast-path in FindMessageEnd fires BEFORE JSON boundary detection,
    /// and it scans data[offset..] which may contain multiple messages.
    ///
    /// Wire data: {"id":1}plain text message\n
    /// Expected:  msg1={"id":1}, msg2=plain text message
    /// Actual:    msg1={"id":1}plain text message (merged!)
    /// </summary>
    [Test]
    public async Task Bug_json_without_newline_followed_by_newline_message_merges()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string json1 = "{\"id\":1}";
        string msg2 = "plain text message";

        // All in one write — receiver sees both messages in one buffer read
        byte[] data = Encoding.UTF8.GetBytes(json1 + msg2 + "\n");
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        // This SHOULD be 2 messages, but the bug causes them to merge into 1
        received.Should().HaveCount(2, "JSON boundary should be detected before newline search spans past it");
        Encoding.UTF8.GetString(received[0]).Should().Be(json1, "First message should be the JSON object");
        Encoding.UTF8.GetString(received[1]).Should().Be(msg2, "Second message should be the text");
    }

    /// <summary>
    /// BUG variant: Two JSON objects without newlines, followed by newline-delimited text.
    /// The \n at the end causes ALL three messages to merge into one.
    ///
    /// Wire data: {"a":1}{"b":2}text\n
    /// Expected:  msg1={"a":1}, msg2={"b":2}, msg3=text
    /// Actual:    msg1={"a":1}{"b":2}text (merged!)
    /// </summary>
    [Test]
    public async Task Bug_two_json_then_newline_text_all_merged()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string json1 = "{\"a\":1}";
        string json2 = "{\"b\":2}";
        string msg3 = "text";

        byte[] data = Encoding.UTF8.GetBytes(json1 + json2 + msg3 + "\n");
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        received.Should().HaveCount(3, "Each message should be detected separately");
        Encoding.UTF8.GetString(received[0]).Should().Be(json1);
        Encoding.UTF8.GetString(received[1]).Should().Be(json2);
        Encoding.UTF8.GetString(received[2]).Should().Be(msg3);
    }

    /// <summary>
    /// BUG variant: JSON without newline followed by JSON WITH newline.
    /// The \n from the second JSON's delimiter causes the first JSON body 
    /// to be merged with the second JSON into one message.
    ///
    /// Wire data: {"a":1}{"b":2}\n
    /// Expected:  msg1={"a":1}, msg2={"b":2}
    /// Actual:    msg1={"a":1}{"b":2} (merged!)
    /// </summary>
    [Test]
    public async Task Bug_json_no_newline_then_json_with_newline_merged()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string json1 = "{\"a\":1}";
        string json2 = "{\"b\":2}";

        byte[] data = Encoding.UTF8.GetBytes(json1 + json2 + "\n");
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        received.Should().HaveCount(2, "Each JSON object should be a separate message");
        Encoding.UTF8.GetString(received[0]).Should().Be(json1);
        Encoding.UTF8.GetString(received[1]).Should().Be(json2);
    }

    /// <summary>
    /// BUG variant: Even with separate writes and a delay between them, if the kernel
    /// coalesces the writes into a single socket read, the newline from the second
    /// message is used as the delimiter for the combined blob.
    /// This test may be flaky if the kernel doesn't coalesce — but the bug is still present
    /// whenever data from multiple messages is read in a single socket operation.
    /// </summary>
    [Test]
    public async Task Bug_json_then_newline_separate_writes_still_merges()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string json1 = "{\"id\":1}";
        string msg2 = "next";

        // Send in two separate writes with delay
        await sendStream.WriteAsync(Encoding.UTF8.GetBytes(json1));
        await Task.Delay(50);
        await sendStream.WriteAsync(Encoding.UTF8.GetBytes(msg2 + "\n"));
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        // When chunks arrive separately, the first ReceiveAsync sees just {"id":1}
        // JSON parsing detects it as complete. This should work.
        received.Should().HaveCount(2);
        Encoding.UTF8.GetString(received[0]).Should().Be(json1);
        Encoding.UTF8.GetString(received[1]).Should().Be(msg2);
    }

    /// <summary>
    /// BUG: With a small buffer, JSON without newline followed by newline-delimited text
    /// still demonstrates the merging bug because FindMessageEnd scans all available data
    /// after CopyTo + socket read fills the buffer.
    /// </summary>
    [TestCase(16)]
    [TestCase(64)]
    [TestCase(256)]
    public async Task Bug_json_then_newline_message_small_buffer(int bufferSize)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string json1 = "{\"x\":1}";
        string msg2 = "text";

        byte[] data = Encoding.UTF8.GetBytes(json1 + msg2 + "\n");
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, bufferSize);

        received.Should().HaveCount(2, $"bufferSize={bufferSize}: JSON boundary should be detected");
        Encoding.UTF8.GetString(received[0]).Should().Be(json1);
        Encoding.UTF8.GetString(received[1]).Should().Be(msg2);
    }

    /// <summary>
    /// Validates that JSON-RPC requests are correctly separated even without newlines
    /// between them. Previously, messages would merge into corrupt JSON.
    /// </summary>
    [Test]
    public async Task Bug_merged_messages_cause_corrupt_json()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        string request1 = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"eth_blockNumber\",\"params\":[]}";
        string request2 = "{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"eth_chainId\",\"params\":[]}";

        // If request1 has no newline and request2 has a newline, they should be separated
        byte[] data = Encoding.UTF8.GetBytes(request1 + request2 + "\n");
        await sendStream.WriteAsync(data);
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, 1024);

        received.Should().HaveCount(2, "Each JSON-RPC request should be a separate message");
        Encoding.UTF8.GetString(received[0]).Should().Be(request1);
        Encoding.UTF8.GetString(received[1]).Should().Be(request2);
        // Both should be valid JSON individually
        JsonDocument.Parse(received[0]);
        JsonDocument.Parse(received[1]);
    }

    /// <summary>
    /// Fuzz-style test: send many JSON messages with various characteristics,
    /// mixing with and without newlines, different sizes, different buffer sizes.
    /// </summary>
    [TestCase(4)]
    [TestCase(16)]
    [TestCase(64)]
    [TestCase(256)]
    [TestCase(4096)]
    public async Task Fuzz_json_and_newline_mixed_messages(int bufferSize)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        Random rng = new(42); // deterministic seed
        List<string> expected = [];
        List<byte> allBytes = [];

        for (int i = 0; i < 30; i++)
        {
            string msg;
            bool useNewline;

            if (rng.Next(3) == 0)
            {
                // JSON message (valid JSON object or array)
                string payload = new string((char)('a' + i % 26), rng.Next(1, 100));
                msg = $"{{\"i\":{i},\"p\":\"{payload}\"}}";
                useNewline = rng.Next(2) == 0; // 50% chance of newline after JSON
            }
            else
            {
                // Plain text message (always needs newline)
                int len = rng.Next(1, 50);
                char[] chars = new char[len];
                for (int j = 0; j < len; j++)
                    chars[j] = (char)('A' + (i * 7 + j) % 26);
                msg = new string(chars);
                useNewline = true;
            }

            expected.Add(msg);
            allBytes.AddRange(Encoding.UTF8.GetBytes(msg));
            if (useNewline)
                allBytes.Add((byte)'\n');
        }

        await sendStream.WriteAsync(allBytes.ToArray());
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesSocketClientStyleAsync(recvStream, bufferSize);

        received.Should().HaveCount(expected.Count, $"bufferSize={bufferSize}");
        for (int i = 0; i < expected.Count; i++)
        {
            Encoding.UTF8.GetString(received[i]).Should().Be(expected[i],
                $"message {i} with bufferSize={bufferSize}");
        }
    }

    #endregion

    #region Real connection stress tests

    /// <summary>
    /// Interleaved small writes from sender with slow reads from receiver.
    /// Ensures no data loss or corruption under timing variations.
    /// </summary>
    [Test]
    public async Task Interleaved_writes_and_slow_reads()
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        const int messageCount = 30;
        List<string> expected = [];

        // Sender: write messages with small pauses
        Task sendTask = Task.Run(async () =>
        {
            for (int i = 0; i < messageCount; i++)
            {
                string msg = $"msg{i:D3}_{new string((char)('a' + i % 26), i + 1)}";
                expected.Add(msg);
                await sendStream.WriteAsync(Encoding.UTF8.GetBytes(msg));
                await sendStream.WriteEndOfMessageAsync();
                if (i % 5 == 0) await Task.Delay(1);
            }
            sendStream.Socket.Shutdown(SocketShutdown.Send);
        });

        // Receiver: read with delays
        List<byte[]> received = [];
        List<byte> current = [];
        byte[] buffer = new byte[7]; // Small buffer

        while (true)
        {
            ReceiveResult rr = await recvStream.ReceiveAsync(buffer);
            if (rr.IsNull || rr.Closed)
            {
                if (current.Count > 0) received.Add(current.ToArray());
                break;
            }

            for (int i = 0; i < rr.Read; i++) current.Add(buffer[i]);

            if (rr.EndOfMessage)
            {
                received.Add(current.ToArray());
                current = [];
            }

            // Simulate slow consumer
            if (received.Count % 3 == 0) await Task.Delay(1);
        }

        await sendTask;

        received.Should().HaveCount(messageCount);
        for (int i = 0; i < messageCount; i++)
        {
            Encoding.UTF8.GetString(received[i]).Should().Be(expected[i]);
        }
    }

    /// <summary>
    /// Test with binary-like content (bytes 0x01-0x09, 0x0B-0xFF, avoiding \n=0x0A)
    /// to ensure no byte is lost or corrupted.
    /// </summary>
    [TestCase(3)]
    [TestCase(8)]
    [TestCase(64)]
    public async Task Binary_content_integrity(int bufferSize)
    {
        var (server, client, cleanup) = await CreateSocketPairAsync();
        using var _ = cleanup;

        await using IpcSocketMessageStream sendStream = new(client);
        await using IpcSocketMessageStream recvStream = new(server);

        List<byte[]> expected = [];
        List<byte> allBytes = [];

        for (int msgIdx = 0; msgIdx < 20; msgIdx++)
        {
            int msgLen = (msgIdx * 3 + 1) % 15 + 1;
            byte[] msg = new byte[msgLen];
            for (int j = 0; j < msgLen; j++)
            {
                byte b = (byte)((msgIdx * 17 + j * 13 + 1) % 255 + 1); // 1-255
                if (b == (byte)'\n') b = 0x0B; // avoid delimiter
                msg[j] = b;
            }
            expected.Add(msg);
            allBytes.AddRange(msg);
            allBytes.Add((byte)'\n');
        }

        await sendStream.WriteAsync(allBytes.ToArray());
        sendStream.Socket.Shutdown(SocketShutdown.Send);

        List<byte[]> received = await ReadAllMessagesAsync(recvStream, bufferSize);

        received.Should().HaveCount(expected.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            received[i].Should().Equal(expected[i], $"binary msg {i} with bufferSize={bufferSize}");
        }
    }

    #endregion
}
