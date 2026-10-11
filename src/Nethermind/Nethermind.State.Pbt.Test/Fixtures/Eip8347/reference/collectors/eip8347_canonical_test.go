package main

import (
	"bytes"
	"encoding/binary"
	"encoding/json"
	"github.com/ethereum/go-ethereum/common"
	"github.com/ethereum/go-ethereum/core/types"
	"github.com/ethereum/go-ethereum/crypto"
	"github.com/ethereum/go-ethereum/rlp"
	"os"
	"path/filepath"
	"sort"
	"testing"
)

// TestExportEip8347Canonical adapts only the preimage wire format. The pinned
// geth remains the independent execution/tree oracle, not a current-wire verifier.
func TestExportEip8347Canonical(t *testing.T) {
	out := os.Getenv("EIP8347_FIXTURE_OUT")
	if out == "" {
		t.Skip("output unset")
	}
	var index []struct {
		Name string `json:"name"`
	}
	read := func(p string) []byte {
		b, e := os.ReadFile(p)
		if e != nil {
			t.Fatal(e)
		}
		return b
	}
	if e := json.Unmarshal(read(filepath.Join(out, "states/index.json")), &index); e != nil {
		t.Fatal(e)
	}
	type result struct {
		Name           string      `json:"name"`
		Digest         common.Hash `json:"preimageDigest"`
		SnapshotDigest common.Hash `json:"snapshotDigest"`
		Accounts       int         `json:"accounts"`
		Verified       bool        `json:"verified"`
	}
	var results []result
	for _, item := range index {
		var alloc types.GenesisAlloc
		if e := json.Unmarshal(read(filepath.Join(out, "states", item.Name+".alloc.json")), &alloc); e != nil {
			t.Fatal(e)
		}
		addresses := make([]common.Address, 0, len(alloc))
		for a := range alloc {
			addresses = append(addresses, a)
		}
		sort.Slice(addresses, func(i, j int) bool {
			return bytes.Compare(crypto.Keccak256(addresses[i][:]), crypto.Keccak256(addresses[j][:])) < 0
		})
		var encoded bytes.Buffer
		for _, a := range addresses {
			slots := make([]common.Hash, 0, len(alloc[a].Storage))
			for k, v := range alloc[a].Storage {
				if v != (common.Hash{}) {
					slots = append(slots, k)
				}
			}
			sort.Slice(slots, func(i, j int) bool {
				return bytes.Compare(crypto.Keccak256(slots[i][:]), crypto.Keccak256(slots[j][:])) < 0
			})
			encoded.Write(a[:])
			var count [4]byte
			binary.BigEndian.PutUint32(count[:], uint32(len(slots)))
			encoded.Write(count[:])
			for _, k := range slots {
				encoded.Write(k[:])
			}
		}
		// Decode independently with bounds, order, uniqueness and exact allocation-set checks.
		verify := func(data []byte) bool {
			seen := map[common.Address]bool{}
			var previous []byte
			for len(data) > 0 {
				if len(data) < 24 {
					return false
				}
				a := common.BytesToAddress(data[:20])
				hash := crypto.Keccak256(a[:])
				if previous != nil && bytes.Compare(previous, hash) >= 0 {
					return false
				}
				previous = hash
				account, ok := alloc[a]
				if !ok || seen[a] {
					return false
				}
				seen[a] = true
				n := uint64(binary.BigEndian.Uint32(data[20:24]))
				data = data[24:]
				if n > uint64(len(data))/32 {
					return false
				}
				keys := map[common.Hash]bool{}
				var prev []byte
				for i := uint64(0); i < n; i++ {
					k := common.BytesToHash(data[:32])
					data = data[32:]
					h := crypto.Keccak256(k[:])
					if prev != nil && bytes.Compare(prev, h) >= 0 {
						return false
					}
					prev = h
					if keys[k] || account.Storage[k] == (common.Hash{}) {
						return false
					}
					keys[k] = true
				}
				expected := 0
				for _, v := range account.Storage {
					if v != (common.Hash{}) {
						expected++
					}
				}
				if len(keys) != expected {
					return false
				}
			}
			return len(seen) == len(alloc)
		}
		canonical := encoded.Bytes()
		if !verify(canonical) {
			t.Fatal("canonical verification failed", item.Name)
		}
		if verify(append(append([]byte{}, canonical...), 0)) {
			t.Fatal("trailing byte accepted")
		}
		if len(canonical) > 0 && verify(canonical[:len(canonical)-1]) {
			t.Fatal("truncation accepted")
		}
		// Independently recover the same source set from geth's legacy RLP file.
		legacy := read(filepath.Join(out, "images", item.Name, "preimages.bin"))
		stream := rlp.NewStream(bytes.NewReader(legacy), uint64(len(legacy)))
		legacySeen := map[common.Address]bool{}
		for len(legacySeen) < len(alloc) {
			var rec struct {
				Address  []byte
				SlotKeys [][]byte
			}
			if e := stream.Decode(&rec); e != nil {
				t.Fatal(e)
			}
			a := common.BytesToAddress(rec.Address)
			account, ok := alloc[a]
			if !ok || legacySeen[a] {
				t.Fatal("legacy account mismatch")
			}
			legacySeen[a] = true
			keys := map[common.Hash]bool{}
			for _, s := range rec.SlotKeys {
				k := common.BytesToHash(s)
				if keys[k] || account.Storage[k] == (common.Hash{}) {
					t.Fatal("legacy slot mismatch")
				}
				keys[k] = true
			}
			if len(keys) != len(account.Storage) {
				t.Fatal("legacy count mismatch")
			}
		}
		dir := filepath.Join(out, "canonical", item.Name)
		if e := os.MkdirAll(dir, 0755); e != nil {
			t.Fatal(e)
		}
		snapshot := read(filepath.Join(out, "images", item.Name, "snapshot.pbt"))
		if e := os.WriteFile(filepath.Join(dir, "preimages.bin"), canonical, 0644); e != nil {
			t.Fatal(e)
		}
		if e := os.WriteFile(filepath.Join(dir, "snapshot.pbt"), snapshot, 0644); e != nil {
			t.Fatal(e)
		}
		results = append(results, result{item.Name, crypto.Keccak256Hash(canonical), crypto.Keccak256Hash(snapshot), len(alloc), true})
	}
	manifest := struct {
		Eips      string   `json:"eips"`
		Method    string   `json:"method"`
		Artifacts []result `json:"artifacts"`
	}{"f3079a09e8c606afcb0e5e1a309ff228b88dc067", "test-only fixed-width Keccak-order encoder; independent bounded parser checks exact state key set; legacy geth verifier remains separate root oracle", results}
	blob, e := json.MarshalIndent(manifest, "", "  ")
	if e != nil {
		t.Fatal(e)
	}
	if e = os.WriteFile(filepath.Join(out, "canonical-manifest.json"), append(blob, '\n'), 0644); e != nil {
		t.Fatal(e)
	}
}
