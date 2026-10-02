# Diagnostic-only bench tweak (never for the gate): with OPCODE_REFRESH_PER_CASE=1 every case restarts the VM's
# process-wide table-refresh counter before its 400k-STOP loop, so the last refresh of the case captures the entry
# points of the handlers its first 100k executions compiled (in process, only the first case of a sweep gets that).
import sys
p = sys.argv[1]
s = open(p, encoding='utf-8').read()
old = '''        // Advance the periodic table refresh after warming the selected workload's instruction bodies.
'''
assert s.count(old) == 1
s = s.replace(old, old + '''        if (Environment.GetEnvironmentVariable("OPCODE_REFRESH_PER_CASE") == "1")
            typeof(VirtualMachine<EthereumGasPolicy>).GetField("_txCount",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, 0L);
''')
open(p, 'w', encoding='utf-8', newline='\n').write(s)
print('refresh tweak applied')
old2 = """        _code = chain;
        _codePool = codePool;
    }
"""
assert s.count(old2) == 1
s = s.replace(old2, """        _code = chain;
        _codePool = codePool;
        if (Environment.GetEnvironmentVariable("OPCODE_DUMP_TABLE") == "1")
        {
            ExecuteContract();
            object table = typeof(VirtualMachine<EthereumGasPolicy>).GetField("_opcodeHandlers",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(_vm)!;
            nint[] entries = System.Runtime.CompilerServices.Unsafe.As<nint[]>(table);
            System.Text.StringBuilder line = new($"TABLE {Chain} {Cancelable} n={entries.Length}");
            for (int i = 0; i < 256; i++)
                if (entries[i] != entries[0x0c]) line.Append($" {i:x2}={(long)entries[i]:x}");
            Console.WriteLine(line.ToString());
        }
    }
""")
open(p, 'w', encoding='utf-8', newline='\n').write(s)
print('dump tweak applied')
