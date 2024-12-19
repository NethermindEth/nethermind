using FluentAssertions;
using Nethermind.Serialization.Rlp.Test.Instances;

namespace Nethermind.Serialization.Rlp.Test;

[Parallelizable(ParallelScope.All)]
public class RlpWriterTest
{
    [Test]
    public void WriteShortString()
    {
        var serialized = Rlp.Write(static writer => { writer.Write("dog"); });

        byte[] expected = [0x83, (byte)'d', (byte)'o', (byte)'g'];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteLongString()
    {
        var serialized = Rlp.Write(writer =>
        {
            writer.Write("Lorem ipsum dolor sit amet, consectetur adipisicing elit");
        });

        byte[] expected = [0xb8, 0x38, .."Lorem ipsum dolor sit amet, consectetur adipisicing elit"u8];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteEmptyString()
    {
        var serialized = Rlp.Write(static writer => { writer.Write(""); });

        byte[] expected = [0x80];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteInteger_1Component()
    {
        for (int i = 0; i < 0x80; i++)
        {
            var integer = i;
            var serialized = Rlp.Write(writer => { writer.Write(integer); });

            byte[] expected = [(byte)integer];
            serialized.Should().BeEquivalentTo(expected);
        }
    }

    [Test]
    public void WriteInteger_2Components()
    {
        byte[] expected = [0x81, 0x00];
        for (int i = 0x80; i < 0x0100; i++)
        {
            var integer = i;
            var serialized = Rlp.Write(writer => { writer.Write(integer); });

            expected[1] = (byte)integer;
            serialized.Should().BeEquivalentTo(expected);
        }
    }

    [Test]
    public void WriteInteger_3Components()
    {
        byte[] expected = [0x82, 0x00, 0x00];
        for (int i = 0x100; i < 0xFFFF; i++)
        {
            var integer = i;
            var serialized = Rlp.Write(writer => { writer.Write(integer); });

            expected[1] = (byte)((integer & 0xFF00) >> 8);
            expected[2] = (byte)((integer & 0x00FF) >> 0);
            serialized.Should().BeEquivalentTo(expected);
        }
    }

    [Test]
    public void WriteStringList()
    {
        var serialized = Rlp.Write(static writer =>
        {
            writer.WriteList(static writer =>
            {
                writer.Write("cat");
                writer.Write("dog");
            });
        });

        byte[] expected = [0xc8, 0x83, (byte)'c', (byte)'a', (byte)'t', 0x83, (byte)'d', (byte)'o', (byte)'g'];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteEmptyList()
    {
        var serialized = Rlp.Write(static writer => { writer.WriteList(static _ => { }); });

        byte[] expected = [0xc0];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteByteArray()
    {
        var serialized = Rlp.Write(static writer => { writer.Write([0x04, 0x00]); });

        byte[] expected = [0x82, 0x04, 0x00];
        serialized.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void WriteSetTheoreticalRepresentation()
    {
        var serialized = Rlp.Write(static writer =>
        {
            writer.WriteList(static root =>
            {
                root.WriteList(static _ => { });
                root.WriteList(static w => { w.WriteList(static _ => { }); });
                root.WriteList(static w =>
                {
                    w.WriteList(static _ => { });
                    w.WriteList(static w => { w.WriteList(static _ => { }); });
                });
            });
        });

        byte[] expected = [0xc7, 0xc0, 0xc1, 0xc0, 0xc3, 0xc0, 0xc1, 0xc0];
        serialized.Should().BeEquivalentTo(expected);
    }

    // [Test]
    // public void WriteSequence()
    // {
    //     var serialized = Rlp.Write(writer =>
    //     {
    //         writer.WriteSequence(static x =>
    //         {
    //             x.WriteSequence(static y => { y.Write(0); });
    //             x.WriteSequence(static y =>
    //             {
    //                 y.Write("Foo");
    //                 y.Write("Bar");
    //             });
    //         });
    //     });
    //
    //     byte[] expected = [];
    //     serialized.Should().BeEquivalentTo(expected);
    // }
}
