#!/usr/bin/env python3
"""Apply one verifier mutant to a worktree: mutate.py <id> <repo>."""
import sys

J = "src/Nethermind/Nethermind.Core/Collections/JournalSet.cs"
T = "src/Nethermind/Nethermind.Evm/StackAccessTracker.cs"

MUTANTS = {
    # JournalSet
    "M1_grow_keeps_stale_kept_array": [(J,
        "                // Reusing the kept array: only the active part can hold entries.\n                Array.Clear(_table, 0, _tableSize);\n",
        "                // Reusing the kept array: only the active part can hold entries.\n")],
    "M2_free_home_slot_only": [(J,
        "            while (Unsafe.Add(ref table, slot).Entry != entry)\n",
        "            while (_items.Count < 0 && Unsafe.Add(ref table, slot).Entry != entry)\n")],
    "M3_sparse_clear_shrinks_without_freeing": [(J,
        "                for (int i = count - 1; i >= 0; i--)\n                {\n                    FreeSlotOf(i);\n                }\n",
        "                for (int i = count - 1; i >= 0 && count < 0; i--)\n                {\n                    FreeSlotOf(i);\n                }\n")],
    "M4_findslot_hash_only": [(J,
        "if (entry == 0 || (slot.Hash == hash && AreEqual(items[entry - 1], item)))",
        "if (entry == 0 || (slot.Hash == hash && (items.Length >= 0 || AreEqual(items[entry - 1], item))))")],
    "M5_add_after_grow_stale_slot": [(J,
        "                Grow();\n                slot = ref FindFreeSlot(hash);\n",
        "                Grow();\n")],
    "M6_grow_reverse_order": [(J,
        "            for (int i = 0; i < hashes.Length; i++)\n            {\n                ref Slot slot = ref FindFreeSlot(hashes[i]);",
        "            for (int i = hashes.Length - 1; i >= 0; i--)\n            {\n                ref Slot slot = ref FindFreeSlot(hashes[i]);")],
    "M7_restore_oldest_first": [(J,
        "            for (int i = count - 1; i > snapshot; i--)\n",
        "            for (int i = snapshot + 1; i < count; i++)\n")],
    # StackAccessTracker
    "M8_restore_keeps_remembered_address": [(T,
        "        public void ForgetWarm()\n        {\n            _hasLastWarmCell = false;\n            _lastWarmAddress = null;\n        }\n",
        "        public void ForgetWarm() => _hasLastWarmCell = false;\n"),
        (T, "        private void Clear()\n        {\n            ForgetWarm();\n",
            "        private void Clear()\n        {\n            ForgetWarm();\n            _lastWarmAddress = null;\n")],
    "M9_pooled_reset_keeps_remembered_address": [(T,
        "        private void Clear()\n        {\n            ForgetWarm();\n",
        "        private void Clear()\n        {\n            _hasLastWarmCell = false;\n")],
    "M10_memo_hit_reports_cold": [(T,
        "if (_lastWarmAddress is not null && _lastWarmAddress.Equals(address)) return false;",
        "if (_lastWarmAddress is not null && _lastWarmAddress.Equals(address)) return true;")],
}


def main():
    mid, repo = sys.argv[1], sys.argv[2]
    for path, old, new in MUTANTS[mid]:
        full = f"{repo}/{path}"
        src = open(full, encoding="utf-8").read()
        if src.count(old) != 1:
            sys.exit(f"{mid}: pattern found {src.count(old)} times in {path}")
        open(full, "w", encoding="utf-8", newline="\n").write(src.replace(old, new))
    print(f"applied {mid}")


if __name__ == "__main__":
    if len(sys.argv) == 2 and sys.argv[1] == "list":
        print(" ".join(MUTANTS))
    else:
        main()
