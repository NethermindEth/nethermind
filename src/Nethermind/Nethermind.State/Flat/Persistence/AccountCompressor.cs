// SPDX-FileCopyrightText: 2025 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.IO;
using System.Reflection;
using System.Threading;
using ZstdSharp;

namespace Nethermind.State.Flat.Persistence;

public class AccountCompressor
{
    private byte[] _zstdDictionary;

    public AccountCompressor()
    {
        LoadZstdDictionary();

    }

    public Compressor CreateCompressor()
    {
        Compressor compressor = new Compressor();
        compressor.LoadDictionary(_zstdDictionary);
        return compressor;
    }

    private Decompressor CreateDecompressor()
    {
        Decompressor decomp = new Decompressor();
        decomp.LoadDictionary(_zstdDictionary);
        return decomp;
    }

    Decompressor? _pooledDecompressor;

    public Decompressor RentDecompresor()
    {
        Decompressor? currentlyPooled = _pooledDecompressor;
        if (currentlyPooled is null) return CreateDecompressor();

        if (Interlocked.CompareExchange(ref _pooledDecompressor, null, currentlyPooled) == null)
        {
            return currentlyPooled;
        }

        return CreateDecompressor();
    }

    public void ReturnDecompressor(Decompressor decompressor)
    {
        Decompressor? currentlyPooled = _pooledDecompressor;
        if (currentlyPooled is not null)
        {
            decompressor.Dispose();
            return;
        }

        if (Interlocked.CompareExchange(ref _pooledDecompressor, currentlyPooled, null) == null)
        {
            return;
        }

        decompressor.Dispose();
    }

    private void LoadZstdDictionary()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Nethermind.State.Flat.Persistence.zstddictionary.bin";
        using Stream? stream = assembly.GetManifestResourceStream(resourceName)
                               ?? throw new InvalidOperationException($"Resource '{resourceName}' not found.");

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        _zstdDictionary =  ms.ToArray();
        Console.Error.WriteLine($"Dictionary size is {_zstdDictionary.Length}");
    }

    /*
    internal void TrainDictionary()
    {
        using var snapshot = _db.CreateSnapshot();

        List<byte[]> data = new List<byte[]>();

        FlatDbColumns[] columnsToTest =
        [
            FlatDbColumns.Account,
            // FlatDbColumns.Storage,
            // FlatDbColumns.StateTopNodes,
            // FlatDbColumns.StateNodes,
            // FlatDbColumns.StorageTopNodes,
            // FlatDbColumns.StorageNodes,
        ];

        Random rand = new Random(0);

        byte[] key = new byte[32];
        byte[] maxKey = new byte[32];
        Keccak.MaxValue.Bytes.CopyTo(maxKey);

        int totalSize = 0;

        foreach (FlatDbColumns column in columnsToTest)
        {
            ISortedKeyValueStore col = snapshot.GetColumn(column) as ISortedKeyValueStore;
            for (int i = 0; i < 10000; i++)
            {
                rand.NextBytes(key);

                using ISortedView view = col.GetViewBetween(key, maxKey);

                if (view.MoveNext())
                {
                    data.Add(view.CurrentValue.ToArray());
                    totalSize += view.CurrentValue.Length;
                }
            }
        }

        Console.Error.WriteLine($"Training dictionary");
        byte[] dictionary = DictBuilder.TrainFromBuffer(data, 1024 * 2);
        Console.Error.WriteLine($"Trained a dictionary of size {dictionary.Length} from {data.Count} samples of total size {totalSize}");

        File.WriteAllBytes(Path.Combine(Directory.GetCurrentDirectory(), "flatdictionary.bin"), dictionary);

        using Compressor compressor = new Compressor();
        compressor.LoadDictionary(dictionary);

        foreach (FlatDbColumns column in columnsToTest)
        {
            int compressed = 0;
            int uncompressed = 0;

            ISortedKeyValueStore col = snapshot.GetColumn(column) as ISortedKeyValueStore;
            for (int i = 0; i < 10000; i++)
            {
                rand.NextBytes(key);

                using ISortedView view = col.GetViewBetween(key, maxKey);

                if (view.MoveNext())
                {
                    data.Add(view.CurrentValue.ToArray());
                    totalSize += view.CurrentValue.Length;
                }
            }
            for (int i = 0; i < 10000; i++)
            {
                rand.NextBytes(key);

                using ISortedView view = col.GetViewBetween(key, maxKey);

                if (view.MoveNext())
                {
                    uncompressed += view.CurrentValue.Length;
                    compressed += compressor.Wrap(view.CurrentValue).Length;
                }
            }

            Console.Error.WriteLine($"Expected ratio for {column} {(double)compressed / uncompressed}. Comppressed {compressed:N}, Uncompressed {uncompressed:N}");
        }
    }
    */
}
