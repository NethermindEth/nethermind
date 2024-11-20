using FE = Nethermind.Verkle.Fields.FrEElement.FrE;

namespace Nethermind.Verkle.Tests.Fields;

public class FrETests
{
    [Test]
    public void TestNegativeValues()
    {
        using IEnumerator<FE> set = FE.GetRandom().GetEnumerator();
        for (int i = 0; i < 1000; i++)
        {
            FE x = set.Current;
            FE y = 0 - x;
            FE z = y + x;
            Assert.That(z.IsZero);
            set.MoveNext();
        }
    }

    [Test]
    public void TestAddition()
    {
        FE X = FE.qElement - 1;
        FE Y = X + X;
        FE Z = Y - X;
        Assert.That(Z.Equals(X));

        using IEnumerator<FE> set = FE.GetRandom().GetEnumerator();
        for (int i = 0; i < 1000; i++)
        {
            FE x = set.Current;
            FE y = x + x + x + x;
            FE z = y - x - x - x - x;
            Assert.That(z.IsZero);
            set.MoveNext();
        }
    }

    [Test]
    public void TestHotPath()
    {
        using IEnumerator<FE> set = FE.GetRandom().GetEnumerator();
        for (int i = 0; i < 1000; i++)
        {
            FE current = set.Current;
            FE x = set.Current * set.Current;
            FE.Inverse(in current, out FE y);

            Assert.That(current.Equals(x * y));
        }
    }

    [Test]
    public void TestInverse()
    {
        using IEnumerator<FE> set = FE.GetRandom().GetEnumerator();
        for (int i = 0; i < 1000; i++)
        {
            FE x = set.Current;
            if (x.IsZero)
            {
                set.MoveNext();
                continue;
            }

            FE.Inverse(x, out FE y);
            FE.Inverse(y, out FE z);
            Assert.That(z.Equals(x));
            set.MoveNext();
        }
    }

    [Test]
    public void ProfileInverseMultiplication()
    {
        using IEnumerator<FE> set = FE.GetRandom().GetEnumerator();
        for (int i = 0; i < 1000; i++)
        {
            FE x = set.Current;
            if (x.IsZero)
            {
                set.MoveNext();
                continue;
            }

            FE.Inverse(x, out FE y);
            FE.MultiplyMod(x, y, out FE z);
            Assert.That(z.IsOne);
            set.MoveNext();
        }
    }

    [Test]
    public void ProfileMultiplication()
    {
        using IEnumerator<FE> set = FE.GetRandom().GetEnumerator();
        for (int i = 0; i < 100000; i++)
        {
            FE x = set.Current;
            if (x.IsZero)
            {
                set.MoveNext();
                continue;
            }

            FE.MultiplyMod(x, x, out FE z);
            set.MoveNext();
        }
    }

    [Test]
    public void TestSerialize()
    {
        using IEnumerator<FE> set = FE.GetRandom().GetEnumerator();
        for (int i = 0; i < 1000; i++)
        {
            FE x = set.Current;
            Span<byte> bytes = x.ToBytes();
            FE elem = FE.FromBytes(bytes.ToArray());
            Assert.That(x.Equals(elem));
            set.MoveNext();
        }
    }

    [Test]
    public void TestSerializeBigEndian()
    {
        using IEnumerator<FE> set = FE.GetRandom().GetEnumerator();
        for (int i = 0; i < 1000; i++)
        {
            FE x = set.Current;
            Span<byte> bytes = x.ToBytesBigEndian();
            FE elem = FE.FromBytes(bytes.ToArray(), true);
            Assert.That(x.Equals(elem));
            set.MoveNext();
        }
    }

    [Test]
    public void TestSqrt()
    {
        using IEnumerator<FE> set = FE.GetRandom().GetEnumerator();
        for (int i = 0; i < 1000; i++)
        {
            FE x = set.Current;
            if (FE.Legendre(x) != 1)
            {
                set.MoveNext();
                continue;
            }

            FE.Sqrt(x, out FE sqrtElem);
            FE.Exp(sqrtElem, 2, out FE res);
            Assert.That(x.Equals(res));
            set.MoveNext();
        }
    }

    [Test]
    public void TestMultiInv()
    {
        FE[] values = { FE.SetElement(1), FE.SetElement(2), FE.SetElement(3) };

        FE[] gotInverse = FE.MultiInverse(values);
        FE?[] expectedInverse = NaiveMultiInverse(values);

        Assert.That(gotInverse.Length == expectedInverse.Length);
        for (int i = 0; i < gotInverse.Length; i++) Assert.That(gotInverse[i].Equals(expectedInverse[i]!.Value));
    }

    private static FE?[] NaiveMultiInverse(IReadOnlyList<FE> values)
    {
        FE?[] res = new FE?[values.Count];
        for (int i = 0; i < values.Count; i++)
        {
            FE.Inverse(values[i], out FE x);
            res[i] = x;
        }

        return res;
    }
}
