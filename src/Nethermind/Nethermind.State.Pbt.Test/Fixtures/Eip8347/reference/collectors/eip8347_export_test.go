package main

import (
	"encoding/json"
	"fmt"
	"math/big"
	"os"
	"path/filepath"
	"testing"

	"github.com/ethereum/go-ethereum/common"
	"github.com/ethereum/go-ethereum/core"
	"github.com/ethereum/go-ethereum/core/rawdb"
	"github.com/ethereum/go-ethereum/core/types"
	"github.com/ethereum/go-ethereum/crypto"
	"github.com/ethereum/go-ethereum/params"
	"github.com/ethereum/go-ethereum/triedb"
	"github.com/ethereum/go-ethereum/triedb/pathdb"
)

// TestExportEip8347Images verifies each independently executed lifecycle state by
// reconstructing its MPT, converting it, and running the strict snapshot importer.
func TestExportEip8347Images(t *testing.T) {
	out := os.Getenv("EIP8347_FIXTURE_OUT")
	if out == "" {
		t.Skip("EIP8347_FIXTURE_OUT not set")
	}
	type entry struct {
		Name      string      `json:"name"`
		MptRoot   common.Hash `json:"mptRoot"`
		PbtRoot   common.Hash `json:"pbtRoot"`
		BlockHash common.Hash `json:"blockHash"`
		Number    uint64      `json:"number"`
	}
	var states []entry
	raw, err := os.ReadFile(filepath.Join(out, "states", "index.json"))
	if err != nil {
		t.Fatal(err)
	}
	if err := json.Unmarshal(raw, &states); err != nil {
		t.Fatal(err)
	}
	if len(states) == 0 {
		t.Fatal("no lifecycle states")
	}
	type artifact struct {
		entry
		Snapshot       string      `json:"snapshot"`
		Preimages      string      `json:"preimages"`
		SnapshotDigest common.Hash `json:"snapshotDigest"`
		PreimageDigest common.Hash `json:"preimageDigest"`
		Verified       bool        `json:"verified"`
	}
	results := make([]artifact, 0, len(states))
	for _, e := range states {
		t.Run(e.Name, func(t *testing.T) {
			var alloc types.GenesisAlloc
			raw, err := os.ReadFile(filepath.Join(out, "states", e.Name+".alloc.json"))
			if err != nil {
				t.Fatal(err)
			}
			if err := json.Unmarshal(raw, &alloc); err != nil {
				t.Fatal(err)
			}
			for addr, a := range alloc {
				if a.Nonce == 0 && (a.Balance == nil || a.Balance.Sign() == 0) && len(a.Code) == 0 {
					t.Fatalf("empty migration account %s", addr)
				}
			}
			dir := filepath.Join(out, "images", e.Name)
			if err := os.MkdirAll(dir, 0755); err != nil {
				t.Fatal(err)
			}
			db := rawdb.NewMemoryDatabase()
			defer db.Close()
			td := triedb.NewDatabase(db, &triedb.Config{Preimages: true, PathDB: pathdb.Defaults})
			genesis := &core.Genesis{Config: params.TestChainConfig, BaseFee: big.NewInt(params.InitialBaseFee), Alloc: alloc}
			anchor := genesis.MustCommit(db, td)
			if anchor.Root() != e.MptRoot {
				t.Fatalf("MPT reconstruction %s != lifecycle %s", anchor.Root(), e.MptRoot)
			}
			td.Close()
			src := triedb.NewDatabase(db, &triedb.Config{Preimages: true, PathDB: pathdb.ReadOnly})
			snapshot, preimages := filepath.Join(dir, "snapshot.pbt"), filepath.Join(dir, "preimages.bin")
			root, err := convertState(db, src, anchor.Root(), conversionOptions{snapshotPath: snapshot, preimagePath: preimages})
			src.Close()
			if err != nil {
				t.Fatal(err)
			}
			if root != e.PbtRoot {
				t.Fatalf("converter %s != lifecycle %s", root, e.PbtRoot)
			}
			// This synthetic MPT header authenticates the reconstructed state for the
			// offline verifier; the manifest separately records the real lifecycle hash.
			// Post-fork state images are parity vectors, not eligible migration anchors.
			header := anchor.Header()
			imported, err := importState(db, importOptions{snapshot: snapshot, preimages: preimages, anchor: header, verifyOnly: true})
			if err != nil {
				t.Fatal(err)
			}
			if imported != root {
				t.Fatalf("verified import root %s != %s", imported, root)
			}
			sb, err := os.ReadFile(snapshot)
			if err != nil {
				t.Fatal(err)
			}
			pb, err := os.ReadFile(preimages)
			if err != nil {
				t.Fatal(err)
			}
			results = append(results, artifact{entry: e, Snapshot: filepath.ToSlash(filepath.Join("images", e.Name, "snapshot.pbt")), Preimages: filepath.ToSlash(filepath.Join("images", e.Name, "preimages.bin")), SnapshotDigest: crypto.Keccak256Hash(sb), PreimageDigest: crypto.Keccak256Hash(pb), Verified: true})
			t.Logf("%s block=%s MPT=%s PBT=%s snapshot=%s preimages=%s", e.Name, e.BlockHash, e.MptRoot, root, crypto.Keccak256Hash(sb), crypto.Keccak256Hash(pb))
		})
	}
	if t.Failed() {
		return
	}
	manifest := struct {
		Version   int        `json:"version"`
		Reference string     `json:"reference"`
		Eips      string     `json:"eips"`
		Method    string     `json:"method"`
		Artifacts []artifact `json:"artifacts"`
	}{1, "e31a37fb88c2b75c0897c033bd3f4279dee42268", "f3079a09e8c606afcb0e5e1a309ff228b88dc067", "reference lifecycle -> enumerated logical state -> independent MPT reconstruction -> convertState -> importState verifyOnly", results}
	blob, err := json.MarshalIndent(manifest, "", "  ")
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(out, "images-manifest.json"), append(blob, '\n'), 0644); err != nil {
		t.Fatal(err)
	}
	fmt.Println("EIP8347 images verified:", len(results))
}
