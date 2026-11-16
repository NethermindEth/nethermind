// SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
// SPDX-License-Identifier: LGPL-3.0-only

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Nethermind.Zkvm.Abstractions;
using NUnit.Framework;

namespace Nethermind.Evm.ZkEvm.Test;

/// <summary>Tests that <see cref="Accelerators.JumpDestBitmap"/> reaches ZisK's precompile only for nonempty bytecode.</summary>
/// <remarks>
/// ZisK cannot prove a zero-length bitmap operation. The JIT resolves an inlined <c>SuppressGCTransition</c> import
/// on entry to its caller, so the host needs a native stub to call even when the call is skipped: the tests build
/// one that counts its calls with GCC, and are ignored where GCC is missing.
/// </remarks>
[NonParallelizable]
public class JumpDestBitmapAcceleratorTests
{
    private const string StubSource = """
        #include <stddef.h>
        #include <stdint.h>

        static int calls;

        __attribute__((visibility("default"))) void zkvm_jump_dest_bitmap(uint64_t *bitmap, const uint8_t *code, size_t length) { calls++; }

        __attribute__((visibility("default"))) int bitmap_call_count(void) { return calls; }
        """;

    private nint _stub;
    private string? _stubDirectory;

    [OneTimeSetUp]
    public void BuildStub()
    {
        _stubDirectory = Directory.CreateTempSubdirectory("jumpdest-bitmap-stub").FullName;
        string source = Path.Combine(_stubDirectory, "stub.c");
        string library = Path.Combine(_stubDirectory, "stub.so");
        File.WriteAllText(source, StubSource);

        try
        {
            using Process gcc = Process.Start(new ProcessStartInfo("gcc", ["-shared", "-fPIC", source, "-o", library]) { RedirectStandardError = true })!;
            string errors = gcc.StandardError.ReadToEnd();
            gcc.WaitForExit();
            Assert.That(gcc.ExitCode, Is.Zero, errors);
        }
        catch (Win32Exception)
        {
            Assert.Ignore("GCC is required to build the native stub.");
        }

        _stub = NativeLibrary.Load(library);
        NativeLibrary.SetDllImportResolver(typeof(Accelerators).Assembly, (name, _, _) => name == "__Internal" ? _stub : 0);
    }

    [OneTimeTearDown]
    public void DeleteStubDirectory()
    {
        // The library stays loaded: the resolver cannot be removed, and the bound import keeps its address.
        if (_stubDirectory is not null)
            Directory.Delete(_stubDirectory, recursive: true);
    }

    [Test]
    public unsafe void Skips_the_precompile_only_for_empty_bytecode()
    {
        int callsBefore = CallCount();

        Accelerators.JumpDestBitmap(null, null, 0);

        ulong bitmap = ulong.MaxValue;
        ulong code = 0;
        Accelerators.JumpDestBitmap(&bitmap, (byte*)&code, 0);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(bitmap, Is.EqualTo(ulong.MaxValue));
            Assert.That(CallCount(), Is.EqualTo(callsBefore));
        }

        Accelerators.JumpDestBitmap(&bitmap, (byte*)&code, 1);

        Assert.That(CallCount(), Is.EqualTo(callsBefore + 1));
    }

    private unsafe int CallCount() =>
        ((delegate* unmanaged<int>)NativeLibrary.GetExport(_stub, "bitmap_call_count"))();
}
