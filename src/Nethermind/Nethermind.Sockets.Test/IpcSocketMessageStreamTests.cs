// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Nethermind.Sockets.Test;

public class IpcSocketMessageStreamTests
{
    private static IEnumerable<TestCaseData> MessageFramingCases()
    {
        yield return new TestCaseData(
            """[{"id":1},{"id":2}]{"id":3}""",
            new[] { """[{"id":1},{"id":2}]""", """{"id":3}""" })
            .SetName("Array_batch_without_delimiter");
        yield return new TestCaseData(
            "[{\"id\":1}]\n{\"id\":2}\n",
            new[] { """[{"id":1}]""", """{"id":2}""" })
            .SetName("Array_batch_with_delimiter");
        yield return new TestCaseData(
            "[]",
            new[] { "[]" })
            .SetName("Empty_array");
        yield return new TestCaseData(
            " \t{\"id\":1}{\"id\":2}",
            new[] { " \t{\"id\":1}", """{"id":2}""" })
            .SetName("Leading_whitespace_before_object");
        yield return new TestCaseData(
            "\r\n[{\"id\":1}]",
            new[] { "\r\n[{\"id\":1}]" })
            .SetName("Whitespace_including_newline_before_array");
    }

    [TestCaseSource(nameof(MessageFramingCases))]
    public async Task Frames_messages_at_json_boundaries(string wireData, string[] expectedMessages)
    {
        using SocketPair pair = await SocketPair.CreateAsync();
        await using IpcSocketMessageStream stream = new(pair.Server);

        await pair.Client.SendAsync(Encoding.UTF8.GetBytes(wireData), SocketFlags.None);

        for (int i = 0; i < expectedMessages.Length; i++)
        {
            byte[] buffer = new byte[1024];
            (int read, ReceiveResult result) = await ReceiveUntilEndOfMessageAsync(stream, buffer, pair.Token);

            Assert.That(result.EndOfMessage, Is.True, $"message {i}");
            Assert.That(Encoding.UTF8.GetString(buffer, 0, read), Is.EqualTo(expectedMessages[i]), $"message {i}");
        }
    }

    [Test]
    public async Task Whitespace_only_data_does_not_complete_a_message()
    {
        using SocketPair pair = await SocketPair.CreateAsync();
        await using IpcSocketMessageStream stream = new(pair.Server);
        byte[] buffer = new byte[1024];

        byte[] whitespace = " \t "u8.ToArray();
        await pair.Client.SendAsync(whitespace, SocketFlags.None);
        int whitespaceRead = await ReceiveWithoutEndOfMessageAsync(stream, buffer, whitespace.Length, pair.Token);

        await pair.Client.SendAsync("""{"id":1}"""u8.ToArray(), SocketFlags.None);
        (int read, ReceiveResult second) = await ReceiveUntilEndOfMessageAsync(stream, buffer, pair.Token, whitespaceRead);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.EndOfMessage, Is.True);
            Assert.That(Encoding.UTF8.GetString(buffer, 0, read), Is.EqualTo(" \t {\"id\":1}"));
        }
    }

    [TestCase("{\"partial\":", "hello\n", "hello", TestName = "Buffer_no_longer_starts_with_json")]
    [TestCase("   {\"p\":", "X\n", "X", TestName = "Saved_offset_beyond_buffer_length")]
    public async Task Saved_parse_state_is_invalidated_when_buffer_is_reused(string firstSend, string secondSend, string expectedMessage)
    {
        using SocketPair pair = await SocketPair.CreateAsync();
        await using IpcSocketMessageStream stream = new(pair.Server);
        byte[] buffer = new byte[1024];

        await pair.Client.SendAsync(Encoding.UTF8.GetBytes(firstSend), SocketFlags.None);
        await ReceiveWithoutEndOfMessageAsync(stream, buffer, firstSend.Length, pair.Token);

        // Reuse the buffer from offset zero — the non-accumulating caller pattern that the
        // saved-state validation in TryGetCompleteJsonLength guards against.
        await pair.Client.SendAsync(Encoding.UTF8.GetBytes(secondSend), SocketFlags.None);
        (int read, ReceiveResult second) = await ReceiveUntilEndOfMessageAsync(stream, buffer, pair.Token);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(second.EndOfMessage, Is.True);
            Assert.That(Encoding.UTF8.GetString(buffer, 0, read), Is.EqualTo(expectedMessage));
        }
    }

    [Test]
    public async Task WriteEndOfMessageAsync_writes_a_single_newline()
    {
        using SocketPair pair = await SocketPair.CreateAsync();
        await using IpcSocketMessageStream stream = new(pair.Server);

        await stream.WriteAsync("""{"id":1}"""u8.ToArray(), pair.Token);
        int written = await stream.WriteEndOfMessageAsync();

        byte[] expected = "{\"id\":1}\n"u8.ToArray();
        byte[] received = new byte[expected.Length];
        int total = 0;
        while (total < expected.Length)
        {
            total += await pair.Client.ReceiveAsync(received.AsMemory(total), SocketFlags.None, pair.Token);
        }

        using (Assert.EnterMultipleScope())
        {
            Assert.That(written, Is.EqualTo(1));
            Assert.That(received, Is.EqualTo(expected));
        }
    }

    [Test]
    public async Task Dispose_twice_returns_pooled_overflow_buffer_only_once()
    {
        const string firstMessage = """{"id":1}""";
        const string overflowMessage = """{"id":2}""";

        using SocketPair pair = await SocketPair.CreateAsync();
        IpcSocketMessageStream stream = new(pair.Server);

        // Two messages in one read force the second into the pooled overflow buffer.
        await pair.Client.SendAsync(Encoding.UTF8.GetBytes(firstMessage + overflowMessage), SocketFlags.None);
        byte[] buffer = new byte[1024];
        ReceiveResult result = await stream.ReceiveAsync(new ArraySegment<byte>(buffer, 0, buffer.Length), pair.Token);

        Assert.That(result.EndOfMessage, Is.True);

        stream.Dispose();
        stream.Dispose();

        // A double return would corrupt the pool and hand out the same array twice.
        byte[] firstRented = ArrayPool<byte>.Shared.Rent(overflowMessage.Length);
        byte[] secondRented = ArrayPool<byte>.Shared.Rent(overflowMessage.Length);

        Assert.That(secondRented, Is.Not.SameAs(firstRented));

        ArrayPool<byte>.Shared.Return(firstRented);
        ArrayPool<byte>.Shared.Return(secondRented);
    }

    /// <summary>
    /// Reads from <paramref name="stream"/> the way <see cref="SocketClient{TStream}"/> does:
    /// accumulating into <paramref name="buffer"/> until a message boundary is reported.
    /// </summary>
    private static async Task<(int TotalRead, ReceiveResult Result)> ReceiveUntilEndOfMessageAsync(
        IpcSocketMessageStream stream, byte[] buffer, CancellationToken token, int offset = 0)
    {
        ReceiveResult result;
        do
        {
            result = await stream.ReceiveAsync(new ArraySegment<byte>(buffer, offset, buffer.Length - offset), token);
            offset += result.Read;
        } while (!result.EndOfMessage && !result.Closed);

        return (offset, result);
    }

    /// <summary>
    /// Accumulates exactly <paramref name="expectedTotal"/> bytes into <paramref name="buffer"/>,
    /// asserting that no message boundary is reported while doing so.
    /// </summary>
    private static async Task<int> ReceiveWithoutEndOfMessageAsync(
        IpcSocketMessageStream stream, byte[] buffer, int expectedTotal, CancellationToken token)
    {
        int offset = 0;
        while (offset < expectedTotal)
        {
            ReceiveResult result = await stream.ReceiveAsync(new ArraySegment<byte>(buffer, offset, buffer.Length - offset), token);

            Assert.That(result.EndOfMessage, Is.False);
            Assert.That(result.Closed, Is.False);

            offset += result.Read;
        }

        return offset;
    }

    private sealed record SocketPair(string SocketPath, Socket Listener, Socket Server, Socket Client, CancellationTokenSource Cts) : IDisposable
    {
        public CancellationToken Token => Cts.Token;

        public static async Task<SocketPair> CreateAsync()
        {
            // Short name: macOS caps sun_path at 104 chars, ~49 of which its temp dir already uses.
            string path = Path.Combine(Path.GetTempPath(), $"nm-ipc-{Guid.NewGuid():N}.sock");
            UnixDomainSocketEndPoint endPoint = new(path);
            Socket listener = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(endPoint);
            listener.Listen(1);
            Socket client = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await client.ConnectAsync(endPoint);
            Socket server = await listener.AcceptAsync();
            return new SocketPair(path, listener, server, client, new CancellationTokenSource(TimeSpan.FromSeconds(10)));
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
            Listener.Dispose();
            Cts.Dispose();
            if (File.Exists(SocketPath))
            {
                File.Delete(SocketPath);
            }
        }
    }
}
