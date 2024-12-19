// SPDX-FileCopyrightText: 2024 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using FluentAssertions;
using Nethermind.Serialization.Rlp.Test.Instances;

namespace Nethermind.Serialization.Rlp.Test;

public class RlpReadWriteTest
{
    [Test]
    public void HeterogeneousList()
    {
        var rlp = Rlp.Write(static w =>
        {
            w.WriteList(static w =>
            {
                w.WriteList(static w => { w.Write(42); });
                w.WriteList(static w =>
                {
                    w.Write("dog");
                    w.Write("cat");
                });
            });
        });

        var decoded = Rlp.Read(rlp, (ref RlpReader r) =>
        {
            return r.ReadList(static (ref RlpReader r) =>
            {
                var _1 = r.ReadList(static (ref RlpReader r) => r.ReadInt32());
                var _2 = r.ReadList(static (ref RlpReader r) =>
                {
                    var _1 = r.ReadString();
                    var _2 = r.ReadString();

                    return (_1, _2);
                });

                return (_1, _2);
            });
        });

        decoded.Should().Be((42, ("dog", "cat")));
    }


    [TestCase(2)]
    public void UnknownLengthList([Values(1, 3, 5, 10, 20)] int length)
    {
        var rlp = Rlp.Write(root =>
        {
            root.WriteList(w =>
            {
                for (int i = 0; i < length; i++)
                {
                    w.Write(42);
                }
            });
        });
        List<int> decoded = Rlp.Read(rlp, (ref RlpReader r) =>
        {
            List<int> result = [];
            r.ReadList((ref RlpReader r) =>
            {
                while (r.HasNext)
                {
                    result.Add(r.ReadInt32());
                }
            });
            return result;
        });

        decoded.Count.Should().Be(length);
    }

    [Test]
    public void InvalidObjectReading()
    {
        var rlp = Rlp.Write(static w => { w.Write(42); });
        Action tryRead = () => Rlp.Read(rlp, (ref RlpReader r) =>
        {
            r.ReadList((ref RlpReader _) => { });
        });

        tryRead.Should().Throw<RlpReaderException>();
    }

    [Test]
    public void InvalidListReading()
    {
        var rlp = Rlp.Write(static w => { w.WriteList(static _ => { }); });
        Func<int> tryRead = () => Rlp.Read(rlp, (ref RlpReader r) => r.ReadInt32());

        tryRead.Should().Throw<RlpReaderException>();
    }
}
