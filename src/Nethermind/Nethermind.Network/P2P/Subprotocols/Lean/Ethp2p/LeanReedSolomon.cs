// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.Diagnostics.CodeAnalysis;

namespace Nethermind.Network.P2P.Subprotocols.Lean.Ethp2p;

/// <summary>The fixed 16-of-32 systematic Reed-Solomon code of EIP-8437 ethp2p broadcast.</summary>
/// <remarks>
/// Arithmetic is GF(256) with polynomial <c>0x11d</c>. With <c>V[r,c] = r**c</c> (and <c>0**0 = 1</c>) for 32 rows and 16
/// columns, the generator is <c>G = V * inverse(V[0:16,:])</c>, so rows 0 to 15 copy the data shards and rows 16 to 31 are
/// parity. Any 16 distinct shards reconstruct the padded body.
/// </remarks>
public static class LeanReedSolomon
{
    public const int DataShards = 16;
    public const int TotalShards = 32;
    public const int MaxBodyBytes = 1 << 20;
    public const int MaxShardBytes = MaxBodyBytes / DataShards;

    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    /// <summary>Products by every coefficient: row <c>c</c> holds <c>c * x</c> for each byte <c>x</c>.</summary>
    private static readonly byte[] Products = new byte[256 * 256];

    /// <summary>The 32 x 16 generator matrix, row-major.</summary>
    internal static readonly byte[,] Generator;

    static LeanReedSolomon()
    {
        int value = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)value;
            Log[value] = (byte)i;
            value <<= 1;
            if ((value & 0x100) != 0) value ^= 0x11d;
        }
        for (int i = 255; i < Exp.Length; i++) Exp[i] = Exp[i - 255];
        for (int a = 1; a < 256; a++)
            for (int b = 1; b < 256; b++) Products[a * 256 + b] = Exp[Log[a] + Log[b]];

        byte[,] vandermonde = new byte[TotalShards, DataShards];
        for (int row = 0; row < TotalShards; row++)
            for (int column = 0; column < DataShards; column++)
                vandermonde[row, column] = Power((byte)row, column);
        byte[,] top = new byte[DataShards, DataShards];
        for (int row = 0; row < DataShards; row++)
            for (int column = 0; column < DataShards; column++)
                top[row, column] = vandermonde[row, column];
        if (!TryInvert(top, out byte[,]? inverse)) throw new InvalidOperationException("Vandermonde top block is singular");
        Generator = Multiply(vandermonde, inverse);
    }

    public static byte Multiply(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte Power(byte a, int exponent)
    {
        byte result = 1;
        for (int i = 0; i < exponent; i++) result = Multiply(result, a);
        return result;
    }

    private static byte Inverse(byte a) => Exp[255 - Log[a]];

    /// <summary>The shard width for a body: <c>ceil(byte_length / 16)</c>.</summary>
    public static int ShardBytes(int bodyLength) => (bodyLength + DataShards - 1) / DataShards;

    /// <summary>Pads a body with zeros to <c>16 * shard_bytes</c> and returns all 32 shards.</summary>
    public static byte[][] Encode(ReadOnlySpan<byte> body)
    {
        if (body.Length is 0 or > MaxBodyBytes) throw new ArgumentException("Broadcast body length is out of bounds", nameof(body));
        int width = ShardBytes(body.Length);
        byte[][] shards = new byte[TotalShards][];
        for (int i = 0; i < DataShards; i++)
        {
            shards[i] = new byte[width];
            int start = i * width;
            if (start < body.Length) body.Slice(start, Math.Min(width, body.Length - start)).CopyTo(shards[i]);
        }
        for (int row = DataShards; row < TotalShards; row++)
        {
            shards[row] = new byte[width];
            Mix(Generator, row, shards, shards[row]);
        }
        return shards;
    }

    /// <summary>Reconstructs the 16 data shards from any 16 distinct shards.</summary>
    /// <param name="indices">16 distinct shard indices below 32.</param>
    /// <param name="shards">The shards at those indices, all of one width.</param>
    /// <returns>The padded body, <c>16 * width</c> bytes.</returns>
    public static byte[] Reconstruct(ReadOnlySpan<int> indices, byte[][] shards)
    {
        if (indices.Length != DataShards || shards.Length != DataShards) throw new ArgumentException("Exactly 16 shards are needed");
        int width = shards[0].Length;
        byte[,] rows = new byte[DataShards, DataShards];
        for (int i = 0; i < DataShards; i++)
        {
            if (shards[i].Length != width) throw new ArgumentException("Shards differ in width");
            for (int column = 0; column < DataShards; column++) rows[i, column] = Generator[indices[i], column];
        }
        if (!TryInvert(rows, out byte[,]? decoder)) throw new ArgumentException("Shard indices are not distinct");
        byte[] padded = new byte[DataShards * width];
        for (int row = 0; row < DataShards; row++) Mix(decoder, row, shards, padded.AsSpan(row * width, width));
        return padded;
    }

    /// <summary>Writes the combination of <paramref name="inputs"/> by one matrix row into <paramref name="output"/>.</summary>
    private static void Mix(byte[,] matrix, int row, byte[][] inputs, Span<byte> output)
    {
        output.Clear();
        for (int column = 0; column < DataShards; column++)
        {
            byte coefficient = matrix[row, column];
            if (coefficient == 0) continue;
            ReadOnlySpan<byte> input = inputs[column];
            ReadOnlySpan<byte> products = Products.AsSpan(coefficient * 256, 256);
            for (int i = 0; i < output.Length; i++) output[i] ^= products[input[i]];
        }
    }

    private static byte[,] Multiply(byte[,] left, byte[,] right)
    {
        int rows = left.GetLength(0), inner = left.GetLength(1), columns = right.GetLength(1);
        byte[,] result = new byte[rows, columns];
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < columns; c++)
            {
                byte sum = 0;
                for (int k = 0; k < inner; k++) sum ^= Multiply(left[r, k], right[k, c]);
                result[r, c] = sum;
            }
        return result;
    }

    /// <summary>Gauss-Jordan inversion over GF(256).</summary>
    private static bool TryInvert(byte[,] matrix, [NotNullWhen(true)] out byte[,]? inverse)
    {
        int size = matrix.GetLength(0);
        byte[,] work = new byte[size, 2 * size];
        for (int r = 0; r < size; r++)
        {
            for (int c = 0; c < size; c++) work[r, c] = matrix[r, c];
            work[r, size + r] = 1;
        }
        for (int column = 0; column < size; column++)
        {
            int pivot = column;
            while (pivot < size && work[pivot, column] == 0) pivot++;
            if (pivot == size)
            {
                inverse = null;
                return false;
            }
            if (pivot != column)
                for (int c = 0; c < 2 * size; c++) (work[pivot, c], work[column, c]) = (work[column, c], work[pivot, c]);
            byte scale = Inverse(work[column, column]);
            for (int c = 0; c < 2 * size; c++) work[column, c] = Multiply(work[column, c], scale);
            for (int r = 0; r < size; r++)
            {
                byte factor = work[r, column];
                if (r == column || factor == 0) continue;
                for (int c = 0; c < 2 * size; c++) work[r, c] ^= Multiply(factor, work[column, c]);
            }
        }
        inverse = new byte[size, size];
        for (int r = 0; r < size; r++)
            for (int c = 0; c < size; c++) inverse[r, c] = work[r, size + c];
        return true;
    }
}
