-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

namespace WorldJournalExtractor

abbrev Address := Nat
abbrev Word := Nat
abbrev Hash := Nat

structure Cell where
  address : Address
  key : Word
  deriving DecidableEq, Repr

structure AccountView where
  nonce : Nat
  balance : Word
  storageRoot : Hash
  codeHash : Hash
  deriving DecidableEq, Repr

structure LogEntry where
  address : Address
  topics : List Word
  data : List Nat
  deriving DecidableEq, Repr

abbrev FiniteMap (K V : Type) := List (K × V)
abbrev FiniteSet (K : Type) := List K

namespace FiniteMap

def lookup [DecidableEq K] : FiniteMap K V → K → Option V
  | [], _ => none
  | (candidate, value) :: tail, key =>
      if candidate = key then some value else lookup tail key

def put [DecidableEq K] : FiniteMap K V → K → V → FiniteMap K V
  | [], key, value => [(key, value)]
  | (candidate, current) :: tail, key, value =>
      if candidate = key then
        (key, value) :: tail
      else
        (candidate, current) :: put tail key value

def erase [DecidableEq K] : FiniteMap K V → K → FiniteMap K V
  | [], _ => []
  | (candidate, value) :: tail, key =>
      if candidate = key then tail else (candidate, value) :: erase tail key

def getD [DecidableEq K] (entries : FiniteMap K V) (key : K) (fallback : V) : V :=
  (lookup entries key).getD fallback

def ExtEq [DecidableEq K] (left right : FiniteMap K V) : Prop :=
  ∀ key, lookup left key = lookup right key

def Canonical [DecidableEq K] (entries : FiniteMap K V) : Prop :=
  (entries.map Prod.fst).Nodup

end FiniteMap

namespace FiniteSet

def contains [DecidableEq K] : FiniteSet K → K → Bool
  | [], _ => false
  | candidate :: tail, key =>
      if candidate = key then true else contains tail key

def insert [DecidableEq K] (entries : FiniteSet K) (key : K) : FiniteSet K :=
  if contains entries key then entries else key :: entries

def ExtEq [DecidableEq K] (left right : FiniteSet K) : Prop :=
  ∀ key, contains left key = contains right key

def Canonical [DecidableEq K] (entries : FiniteSet K) : Prop := entries.Nodup

end FiniteSet

structure Journal (α : Type) where
  initial : α
  changes : List α
  deriving DecidableEq, Repr

namespace Journal

def current : Journal α → α
  | ⟨initial, []⟩ => initial
  | ⟨_, value :: _⟩ => value

def push (journal : Journal α) (value : α) : Journal α :=
  { journal with changes := value :: journal.changes }

def position (journal : Journal α) : Nat := journal.changes.length

def restore (journal : Journal α) (position : Nat) : Journal α :=
  { journal with changes := journal.changes.drop (journal.changes.length - position) }

end Journal

abbrev AccountMap := FiniteMap Address AccountView
abbrev StorageMap := FiniteMap Cell Word
abbrev AddressSet := FiniteSet Address
abbrev CellSet := FiniteSet Cell

inductive AccountChangeKind where
  | justCache
  | semantic
  deriving BEq, DecidableEq, Repr

structure AccountChange where
  kind : AccountChangeKind
  address : Address
  value : Option AccountView
  hadPrevious : Bool
  deriving DecidableEq, Repr

structure AccountJournal where
  initial : AccountMap
  changes : List AccountChange
  deriving DecidableEq, Repr

namespace AccountJournal

def findHead : List AccountChange → Address → Option AccountChange
  | [], _ => none
  | change :: tail, address =>
      if change.address = address then some change else findHead tail address

def applyChange (accounts : AccountMap) (change : AccountChange) : AccountMap :=
  match change.kind with
  | .justCache => accounts
  | .semantic =>
      match change.value with
      | some account => FiniteMap.put accounts change.address account
      | none => FiniteMap.erase accounts change.address

def current (journal : AccountJournal) : AccountMap :=
  journal.changes.reverse.foldl applyChange journal.initial

def position (journal : AccountJournal) : Nat := journal.changes.length

def pushSemantic (journal : AccountJournal) (address : Address)
    (value : Option AccountView) : AccountJournal :=
  let hadPrevious := (findHead journal.changes address).isSome
  { journal with changes :=
      { kind := .semantic, address, value, hadPrevious } :: journal.changes }

def pushJustCache (journal : AccountJournal) (address : Address)
    (account : AccountView) : AccountJournal :=
  let hadPrevious := (findHead journal.changes address).isSome
  { journal with changes :=
      { kind := .justCache, address, value := some account, hadPrevious } ::
        journal.changes }

def retainedCacheEntry (change : AccountChange) : Bool :=
  change.kind == .justCache && !change.hadPrevious

def restore (journal : AccountJournal) (position : Nat) : AccountJournal :=
  let removeCount := journal.changes.length - position
  let removed := journal.changes.take removeCount
  let retained := journal.changes.drop removeCount
  let reappended := (removed.filter retainedCacheEntry).reverse
  { journal with changes := reappended ++ retained }

end AccountJournal

structure WorldProjection where
  accounts : AccountMap
  persistent : StorageMap
  persistentOriginals : StorageMap
  transient : StorageMap
  warmAccounts : AddressSet
  warmCells : CellSet
  logs : List LogEntry
  destroySet : AddressSet
  createdThisTx : AddressSet
  deriving DecidableEq, Repr

structure FrameProjection where
  accounts : AccountMap
  persistent : StorageMap
  transient : StorageMap
  warmAccounts : AddressSet
  warmCells : CellSet
  logs : List LogEntry
  destroySet : AddressSet
  deriving DecidableEq, Repr

def WorldProjection.ExtEq (left right : WorldProjection) : Prop :=
  FiniteMap.ExtEq left.accounts right.accounts ∧
  FiniteMap.ExtEq left.persistent right.persistent ∧
  FiniteMap.ExtEq left.persistentOriginals right.persistentOriginals ∧
  FiniteMap.ExtEq left.transient right.transient ∧
  FiniteSet.ExtEq left.warmAccounts right.warmAccounts ∧
  FiniteSet.ExtEq left.warmCells right.warmCells ∧
  left.logs = right.logs ∧
  FiniteSet.ExtEq left.destroySet right.destroySet ∧
  FiniteSet.ExtEq left.createdThisTx right.createdThisTx

def WorldProjection.WellFormed (projection : WorldProjection) : Prop :=
  FiniteMap.Canonical projection.accounts ∧
  FiniteMap.Canonical projection.persistent ∧
  FiniteMap.Canonical projection.persistentOriginals ∧
  FiniteMap.Canonical projection.transient ∧
  FiniteSet.Canonical projection.warmAccounts ∧
  FiniteSet.Canonical projection.warmCells ∧
  FiniteSet.Canonical projection.destroySet ∧
  FiniteSet.Canonical projection.createdThisTx

structure FrameSnapshot where
  accounts : Nat
  persistent : Nat
  transient : Nat
  warmAccounts : Nat
  warmCells : Nat
  logs : Nat
  destroySet : Nat
  deriving DecidableEq, Repr

structure JournalState where
  accounts : AccountJournal
  persistent : Journal StorageMap
  persistentOriginals : StorageMap
  transient : Journal StorageMap
  warmAccounts : Journal AddressSet
  warmCells : Journal CellSet
  logs : Journal (List LogEntry)
  destroySet : Journal AddressSet
  createdThisTx : AddressSet
  deriving DecidableEq, Repr

def JournalState.project (state : JournalState) : WorldProjection :=
  { accounts := state.accounts.current
    persistent := state.persistent.current
    persistentOriginals := state.persistentOriginals
    transient := state.transient.current
    warmAccounts := state.warmAccounts.current
    warmCells := state.warmCells.current
    logs := state.logs.current
    destroySet := state.destroySet.current
    createdThisTx := state.createdThisTx }

def JournalState.frameProjection (state : JournalState) : FrameProjection :=
  { accounts := state.accounts.current
    persistent := state.persistent.current
    transient := state.transient.current
    warmAccounts := state.warmAccounts.current
    warmCells := state.warmCells.current
    logs := state.logs.current
    destroySet := state.destroySet.current }

def JournalState.capture (state : JournalState) : FrameSnapshot :=
  { accounts := state.accounts.position
    persistent := state.persistent.position
    transient := state.transient.position
    warmAccounts := state.warmAccounts.position
    warmCells := state.warmCells.position
    logs := state.logs.position
    destroySet := state.destroySet.position }

def JournalState.restore (state : JournalState) (snapshot : FrameSnapshot) : JournalState :=
  { accounts := state.accounts.restore snapshot.accounts
    persistent := state.persistent.restore snapshot.persistent
    persistentOriginals := state.persistentOriginals
    transient := state.transient.restore snapshot.transient
    warmAccounts := state.warmAccounts.restore snapshot.warmAccounts
    warmCells := state.warmCells.restore snapshot.warmCells
    logs := state.logs.restore snapshot.logs
    destroySet := state.destroySet.restore snapshot.destroySet
    createdThisTx := state.createdThisTx }

inductive Observation where
  | account (address : Address) (value : Option AccountView)
  | persistent (cell : Cell) (current original : Word)
  | transient (cell : Cell) (current : Word)
  | warmedAccount (address : Address) (inserted : Bool)
  | warmedCell (cell : Cell) (inserted : Bool)
  | snapshotTaken (snapshot : FrameSnapshot)
  | snapshotRestored (snapshot : FrameSnapshot)
  deriving DecidableEq, Repr

inductive Operation where
  | readAccount (address : Address)
  | createAccount (address : Address) (balance : Word) (nonce : Nat)
  | updateAccount (address : Address) (account : AccountView)
  | deleteAccount (address : Address)
  | readPersistent (cell : Cell)
  | writePersistent (cell : Cell) (value : Word)
  | readTransient (cell : Cell)
  | writeTransient (cell : Cell) (value : Word)
  | warmAccount (address : Address)
  | warmCell (cell : Cell)
  | appendLog (entry : LogEntry)
  | addDestroy (address : Address)
  | takeSnapshot
  | restoreSnapshot
  deriving DecidableEq, Repr

structure Machine where
  journal : JournalState
  snapshots : List FrameSnapshot
  observations : List Observation
  deriving DecidableEq, Repr

structure RuntimePremises where
  dictionaryAndHashSetAreExtensional : Prop
  listAndCollectionsMarshalRestoreSuffix : Prop
  admittedByteArraysRemainImmutable : Prop
  productionValuesRespectFixedWidths : Prop
  backingInputsAgreeAtEntry : Prop
  pinnedEmptyHashesAgree : Prop
  persistentWriteAncillaryEffectsAreProjectionStutters : Prop

def RuntimePremises.Hold (premises : RuntimePremises) : Prop :=
  premises.dictionaryAndHashSetAreExtensional ∧
  premises.listAndCollectionsMarshalRestoreSuffix ∧
  premises.admittedByteArraysRemainImmutable ∧
  premises.productionValuesRespectFixedWidths ∧
  premises.backingInputsAgreeAtEntry ∧
  premises.pinnedEmptyHashesAgree ∧
  premises.persistentWriteAncillaryEffectsAreProjectionStutters

def emptyStorageRoot : Hash :=
  0x56e81f171bcc55a6ff8345e692c0f86e5b48e01b996cadc001622fb5e363b421

def emptyCodeHash : Hash :=
  0xc5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470

def emptyAccount : AccountView :=
  { nonce := 0, balance := 0, storageRoot := emptyStorageRoot, codeHash := emptyCodeHash }

def emptyJournalState : JournalState :=
  { accounts := ⟨[], []⟩
    persistent := ⟨[], []⟩
    persistentOriginals := []
    transient := ⟨[], []⟩
    warmAccounts := ⟨[], []⟩
    warmCells := ⟨[], []⟩
    logs := ⟨[], []⟩
    destroySet := ⟨[], []⟩
    createdThisTx := [] }

def emptyMachine : Machine :=
  { journal := emptyJournalState, snapshots := [], observations := [] }

end WorldJournalExtractor
