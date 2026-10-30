package catalyst

import (
	"bytes"
	"context"
	"encoding/json"
	"math/big"
	"os"
	"path/filepath"
	"testing"

	"github.com/ethereum/go-ethereum/beacon/engine"
	"github.com/ethereum/go-ethereum/common"
	"github.com/ethereum/go-ethereum/common/hexutil"
	"github.com/ethereum/go-ethereum/core/rawdb"
	"github.com/ethereum/go-ethereum/core/txpool"
	"github.com/ethereum/go-ethereum/core/types"
	"github.com/ethereum/go-ethereum/crypto"
	"github.com/ethereum/go-ethereum/params"
	"github.com/ethereum/go-ethereum/rlp"
	"github.com/holiman/uint256"
)

type eip8347StateIndex struct {
	Name      string      `json:"name"`
	MPTRoot   common.Hash `json:"mptRoot"`
	PBTRoot   common.Hash `json:"pbtRoot"`
	BlockHash common.Hash `json:"blockHash"`
	Number    uint64      `json:"number"`
}

type eip8347BlockExport struct {
	eip8347StateIndex
	ParentHash        common.Hash     `json:"parentHash"`
	HeaderRoot        common.Hash     `json:"headerRoot"`
	ShadowRoot        common.Hash     `json:"shadowRoot"`
	Binary            bool            `json:"binary"`
	BlockRLP          hexutil.Bytes   `json:"blockRlp"`
	HeaderRLP         hexutil.Bytes   `json:"headerRlp"`
	BALRLP            hexutil.Bytes   `json:"balRlp"`
	BALHash           *common.Hash    `json:"balHash"`
	Transactions      []hexutil.Bytes `json:"transactions"`
	TransactionHashes []common.Hash   `json:"transactionHashes"`
	Scenario          string          `json:"scenario"`
}

// TestExportEip8347Lifecycle is an opt-in collector for reference e31a37fb88c2b75c0897c033bd3f4279dee42268.
func TestExportEip8347Lifecycle(t *testing.T) {
	out := os.Getenv("EIP8347_FIXTURE_OUT")
	if out == "" {
		t.Skip("set EIP8347_FIXTURE_OUT to export the lifecycle")
	}
	if err := os.MkdirAll(filepath.Join(out, "states"), 0755); err != nil {
		t.Fatal(err)
	}
	writeJSON := func(name string, value any) {
		data, err := json.MarshalIndent(value, "", "  ")
		if err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(filepath.Join(out, name), append(data, '\n'), 0644); err != nil {
			t.Fatal(err)
		}
	}
	encode := func(value any) []byte {
		data, err := rlp.EncodeToBytes(value)
		if err != nil {
			t.Fatal(err)
		}
		return data
	}
	genesis := migrationTestGenesis()
	writer := common.HexToAddress("0x1000000000000000000000000000000000000001")
	twin := common.HexToAddress("0x1000000000000000000000000000000000000002")
	recipient := common.HexToAddress("0x2000000000000000000000000000000000000001")
	authorityKey, err := crypto.HexToECDSA("0000000000000000000000000000000000000000000000000000000000000002")
	if err != nil {
		t.Fatal(err)
	}
	authority := crypto.PubkeyToAddress(authorityKey.PublicKey)
	code := common.FromHex("0x60003560005500") // Store the calldata word at slot zero.
	genesis.Alloc[writer] = types.Account{Nonce: 1, Code: code, Balance: big.NewInt(0)}
	genesis.Alloc[twin] = types.Account{Nonce: 1, Code: code, Balance: big.NewInt(0)}
	genesis.Alloc[authority] = types.Account{Balance: big.NewInt(1)}
	for address, account := range genesis.Alloc {
		if account.Nonce == 0 && len(account.Code) == 0 && (account.Balance == nil || account.Balance.Sign() == 0) {
			t.Fatalf("empty genesis account %s", address)
		}
	}
	writeJSON("genesis.json", genesis)
	writeJSON("scenario.json", map[string]any{"referenceCommit": "e31a37fb88c2b75c0897c033bd3f4279dee42268", "sender": testAddr, "recipient": recipient, "writer": writer, "sharedCodeTwin": twin, "authority": authority, "forkTimestamp": 48, "deletion": "storage slot zero becomes zero; no account deletion claimed", "reorg": "A5 -> common A1 -> B2..B6, independently re-crossing at B4"})
	n, service := startEthService(t, genesis, nil)
	defer n.Close()
	chain := service.BlockChain()
	api := NewConsensusAPI(service)
	signer := types.LatestSigner(genesis.Config)
	var states []eip8347StateIndex
	var exported []eip8347BlockExport
	export := func(name, scenario string, header *types.Header) {
		t.Helper()
		awaitShadowReady(t, chain, header)
		shadow, ok := rawdb.ReadShadowStateRoot(service.ChainDb(), header.Hash(), header.Number.Uint64())
		if !ok {
			t.Fatal("missing shadow")
		}
		binary := chain.Config().IsBinaryTrie(header.Number, header.Time)
		mpt, pbt := header.Root, shadow
		if binary {
			mpt, pbt = shadow, header.Root
		}
		if want := convertCanonicalMerkle(t, chain, service.ChainDb(), genesis, header); want != mpt {
			t.Fatalf("%s reverse MPT %s != %s", name, want, mpt)
		}
		src, err := chain.StateAt(header)
		if err != nil {
			t.Fatal(err)
		}
		universe := make(map[common.Address]map[common.Hash]bool)
		touch := func(address common.Address) map[common.Hash]bool {
			if universe[address] == nil {
				universe[address] = make(map[common.Hash]bool)
			}
			return universe[address]
		}
		for address, account := range genesis.Alloc {
			slots := touch(address)
			for slot := range account.Storage {
				slots[slot] = true
			}
		}
		for number := uint64(1); number <= header.Number.Uint64(); number++ {
			list := rawdb.ReadAccessList(service.ChainDb(), chain.GetCanonicalHash(number), number)
			if list == nil {
				t.Fatalf("missing BAL %d", number)
			}
			for _, account := range *list {
				slots := touch(account.Address)
				for _, change := range account.StorageChanges {
					slots[common.Hash(change.Slot.Bytes32())] = true
				}
				for _, slot := range account.StorageReads {
					slots[common.Hash(slot.Bytes32())] = true
				}
			}
		}
		alloc := make(types.GenesisAlloc)
		for address, slots := range universe {
			if !src.Exist(address) {
				continue
			}
			account := types.Account{Balance: src.GetBalance(address).ToBig(), Nonce: src.GetNonce(address), Code: src.GetCode(address), Storage: make(map[common.Hash]common.Hash)}
			for slot := range slots {
				if value := src.GetState(address, slot); value != (common.Hash{}) {
					account.Storage[slot] = value
				}
			}
			alloc[address] = account
		}
		index := eip8347StateIndex{Name: name, MPTRoot: mpt, PBTRoot: pbt, BlockHash: header.Hash(), Number: header.Number.Uint64()}
		states = append(states, index)
		writeJSON(filepath.Join("states", name+".alloc.json"), alloc)
		block := chain.GetBlockByHash(header.Hash())
		item := eip8347BlockExport{eip8347StateIndex: index, ParentHash: header.ParentHash, HeaderRoot: header.Root, ShadowRoot: shadow, Binary: binary, BlockRLP: encode(block), HeaderRLP: encode(header), Scenario: scenario, Transactions: []hexutil.Bytes{}, TransactionHashes: []common.Hash{}}
		if header.Number.Sign() != 0 {
			list := rawdb.ReadAccessList(service.ChainDb(), header.Hash(), header.Number.Uint64())
			item.BALRLP = encode(list)
			item.BALHash = header.BlockAccessListHash
			if item.BALHash == nil || list.Hash() != *item.BALHash {
				t.Fatal("BAL/header hash mismatch")
			}
		}
		for _, tx := range block.Transactions() {
			data, err := tx.MarshalBinary()
			if err != nil {
				t.Fatal(err)
			}
			item.Transactions = append(item.Transactions, data)
			item.TransactionHashes = append(item.TransactionHashes, tx.Hash())
		}
		exported = append(exported, item)
	}
	export("anchor", "MPT genesis, PBT follower anchor", chain.CurrentBlock())
	txFor := func(to common.Address, value int64, data []byte, delegate *common.Address) *types.Transaction {
		src, err := chain.State()
		if err != nil {
			t.Fatal(err)
		}
		nonce := src.GetNonce(testAddr)
		if delegate == nil {
			return types.MustSignNewTx(testKey, signer, &types.LegacyTx{Nonce: nonce, To: &to, Value: big.NewInt(value), Data: data, Gas: 1000000, GasPrice: big.NewInt(2 * params.InitialBaseFee)})
		}
		auth, err := types.SignSetCode(authorityKey, types.SetCodeAuthorization{ChainID: *uint256.MustFromBig(genesis.Config.ChainID), Address: *delegate, Nonce: src.GetNonce(authority)})
		if err != nil {
			t.Fatal(err)
		}
		return types.MustSignNewTx(testKey, signer, &types.SetCodeTx{ChainID: uint256.MustFromBig(genesis.Config.ChainID), Nonce: nonce, GasTipCap: uint256.NewInt(params.InitialBaseFee), GasFeeCap: uint256.NewInt(2 * params.InitialBaseFee), Gas: 1000000, To: to, Value: uint256.NewInt(0), AuthList: []types.SetCodeAuthorization{auth}})
	}
	build := func(name, scenario string, random common.Hash, tx *types.Transaction) *types.Header {
		service.TxPool().Clear()
		if tx != nil {
			if errs := service.TxPool().Add([]*types.Transaction{tx}, true); len(errs) != 1 || errs[0] != nil {
				t.Fatalf("add %s: %v", name, errs)
			}
			if err := service.TxPool().Sync(); err != nil {
				t.Fatal(err)
			}
			pending, count := service.TxPool().Pending(txpool.PendingFilter{})
			if count != 1 || len(pending[testAddr]) != 1 {
				t.Fatalf("%s pending count %d", name, count)
			}
		}
		parent := chain.CurrentBlock()
		header := buildBlock(t, api, parent, parent.Number.Uint64()+1, random)
		block := chain.GetBlockByHash(header.Hash())
		expected := 0
		if tx != nil {
			expected = 1
		}
		if len(block.Transactions()) != expected {
			t.Fatalf("%s included %d transactions, want %d", name, len(block.Transactions()), expected)
		}
		if tx != nil {
			if block.Transactions()[0].Hash() != tx.Hash() {
				t.Fatalf("%s wrong transaction", name)
			}
			receipts := chain.GetReceiptsByHash(header.Hash())
			if len(receipts) != 1 || receipts[0].Status != types.ReceiptStatusSuccessful {
				if len(receipts) == 1 {
					t.Logf("receipt: %+v", *receipts[0])
				}
				t.Fatalf("%s transaction failed: receipts=%+v count=%d", name, receipts, len(receipts))
			}
		}
		export(name, scenario, header)
		return header
	}
	check := func(slot uint64, delegated *common.Address) {
		src, err := chain.State()
		if err != nil {
			t.Fatal(err)
		}
		if src.GetBalance(recipient).Uint64() != 1000 || src.GetNonce(recipient) != 0 || len(src.GetCode(recipient)) != 0 {
			t.Fatal("fresh balance-only recipient mismatch")
		}
		if src.GetState(writer, common.Hash{}) != common.BigToHash(new(big.Int).SetUint64(slot)) {
			t.Fatal("storage mismatch")
		}
		want := []byte(nil)
		if delegated != nil {
			want = types.AddressToDelegation(*delegated)
		}
		if !bytes.Equal(src.GetCode(authority), want) {
			t.Fatal("delegation mismatch")
		}
		if !bytes.Equal(src.GetCode(writer), src.GetCode(twin)) {
			t.Fatal("shared code mismatch")
		}
	}
	src, err := chain.State()
	if err != nil {
		t.Fatal(err)
	}
	if src.Exist(recipient) {
		t.Fatal("recipient must not exist in genesis")
	}
	commonParent := build("a1", "fresh balance-only recipient", common.Hash{}, txFor(recipient, 1000, nil, nil))
	check(0, nil)
	word := common.BigToHash(big.NewInt(7))
	build("a2", "storage insertion, shared code retained", common.Hash{}, txFor(writer, 0, word[:], nil))
	check(7, nil)
	build("a3", "delegation installed, boundary parent", common.Hash{}, txFor(recipient, 0, nil, &writer))
	check(7, &writer)
	build("a4", "first PBT child, storage deletion", common.Hash{}, txFor(writer, 0, make([]byte, 32), nil))
	check(0, &writer)
	zero := common.Address{}
	build("a5", "delegation cleared", common.Hash{}, txFor(recipient, 0, nil, &zero))
	check(0, nil)
	status, err := api.ForkchoiceUpdatedV4(context.Background(), engine.ForkchoiceStateV1{HeadBlockHash: commonParent.Hash()}, nil, nil)
	if err != nil || status.PayloadStatus.Status != engine.VALID {
		t.Fatalf("rewind: %+v %v", status, err)
	}
	if chain.CurrentBlock().Hash() != commonParent.Hash() {
		t.Fatal("rewind did not select common parent")
	}
	service.TxPool().Clear()
	word = common.BigToHash(big.NewInt(9))
	build("b2", "alternate pre-fork storage insertion", common.Hash{0xbb}, txFor(writer, 0, word[:], nil))
	check(9, nil)
	build("b3", "alternate boundary parent delegates to shared-code twin", common.Hash{0xbb}, txFor(recipient, 0, nil, &twin))
	check(9, &twin)
	build("b4", "alternate first PBT child, storage deletion", common.Hash{0xbb}, txFor(writer, 0, make([]byte, 32), nil))
	check(0, &twin)
	build("b5", "alternate delegation cleared", common.Hash{0xbb}, txFor(recipient, 0, nil, &zero))
	check(0, nil)
	build("b6", "alternate branch outgrows A", common.Hash{0xbb}, nil)
	check(0, nil)
	if !chain.Migrating() {
		t.Fatal("migration window closed during straddle")
	}
	writeJSON("blocks.json", exported)
	writeJSON(filepath.Join("states", "index.json"), states)
}
