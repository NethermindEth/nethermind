-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated from deserialized source-derived IR. Do not edit.
-- Extractor version: 1.2.0
-- Canonical IR SHA-256: 1c6ca0fccb91aa71607f6c7e7da6dd5376a6b75011ffb67b43cc56b8438b08a9
-- Source closure SHA-256: fe2631d0ae4ec334989090d48c34f15f8a65fa60abc8a3117ccf80c39e489524

import Init.Data.String.Search

namespace Eip803x.Generated.MemoryControlOpcodeKernel

inductive Opcode where
  | stop | calldataload | calldatacopy | codecopy | returndatacopy | pop | mload | mstore
  | mstore8 | jump | jumpi | pc | msize | gas | jumpdest | mcopy
  | push0 | push1 | push2 | push3 | push4 | push5 | push6 | push7
  | push8 | push9 | push10 | push11 | push12 | push13 | push14 | push15
  | push16 | push17 | push18 | push19 | push20 | push21 | push22 | push23
  | push24 | push25 | push26 | push27 | push28 | push29 | push30 | push31
  | push32 | dup1 | dup2 | dup3 | dup4 | dup5 | dup6 | dup7
  | dup8 | dup9 | dup10 | dup11 | dup12 | dup13 | dup14 | dup15
  | dup16 | swap1 | swap2 | swap3 | swap4 | swap5 | swap6 | swap7
  | swap8 | swap9 | swap10 | swap11 | swap12 | swap13 | swap14 | swap15
  | swap16 | return | revert | invalid
  deriving DecidableEq, Repr

inductive HandlerRoot where
  | terminating | ordinary | jumpIf
  deriving DecidableEq, Repr

inductive GasClass where
  | zero | veryLow | base | mid | high | jumpdest
  deriving DecidableEq, Repr

inductive DynamicGas where
  | none | copyWords
  deriving DecidableEq, Repr

inductive MemoryAccess where
  | none | copyDestination | word32 | byte1 | memoryCopy | returnRange
  deriving DecidableEq, Repr

inductive ExitKind where
  | stop | continue | jump | conditionalJump | returnData | revertData | invalid
  deriving DecidableEq, Repr

inductive Activation where
  | unconditional | eip211 | eip5656 | eip3855 | eip140
  deriving DecidableEq, Repr

inductive DispatchTable where
  | noTrace | noTraceCancelable | traced | tracedCancelable
  deriving DecidableEq, Repr

structure Descriptor where
  opcodeByte : Nat
  handlerBody : String
  handlerRoot : HandlerRoot
  gasClass : GasClass
  fixedGas : Nat
  dynamicGas : DynamicGas
  memoryAccess : MemoryAccess
  exitKind : ExitKind
  stackInputs : Nat
  stackGrowth : Nat
  immediateWidth : Nat
  activation : Activation
  deriving DecidableEq, Repr

structure Specialization where
  opcode : Opcode
  table : DispatchTable
  tracingFlag : String
  cancelableFlag : String
  closedRoot : String
  deriving DecidableEq, Repr

def allOpcodes : List Opcode :=
  [
    .stop,
    .calldataload,
    .calldatacopy,
    .codecopy,
    .returndatacopy,
    .pop,
    .mload,
    .mstore,
    .mstore8,
    .jump,
    .jumpi,
    .pc,
    .msize,
    .gas,
    .jumpdest,
    .mcopy,
    .push0,
    .push1,
    .push2,
    .push3,
    .push4,
    .push5,
    .push6,
    .push7,
    .push8,
    .push9,
    .push10,
    .push11,
    .push12,
    .push13,
    .push14,
    .push15,
    .push16,
    .push17,
    .push18,
    .push19,
    .push20,
    .push21,
    .push22,
    .push23,
    .push24,
    .push25,
    .push26,
    .push27,
    .push28,
    .push29,
    .push30,
    .push31,
    .push32,
    .dup1,
    .dup2,
    .dup3,
    .dup4,
    .dup5,
    .dup6,
    .dup7,
    .dup8,
    .dup9,
    .dup10,
    .dup11,
    .dup12,
    .dup13,
    .dup14,
    .dup15,
    .dup16,
    .swap1,
    .swap2,
    .swap3,
    .swap4,
    .swap5,
    .swap6,
    .swap7,
    .swap8,
    .swap9,
    .swap10,
    .swap11,
    .swap12,
    .swap13,
    .swap14,
    .swap15,
    .swap16,
    .return,
    .revert,
    .invalid
  ]

def allDispatchTables : List DispatchTable :=
  [.noTrace, .noTraceCancelable, .traced, .tracedCancelable]

def descriptor : Opcode → Descriptor
  | .stop =>
    {
      opcodeByte := 0
      handlerBody := "StopOpcode"
      handlerRoot := .terminating
      gasClass := .zero
      fixedGas := 0
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .stop
      stackInputs := 0
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .calldataload =>
    {
      opcodeByte := 53
      handlerBody := "CallDataLoadOpcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 1
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .calldatacopy =>
    {
      opcodeByte := 55
      handlerBody := "CallDataCopyOpcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .copyWords
      memoryAccess := .copyDestination
      exitKind := .continue
      stackInputs := 3
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .codecopy =>
    {
      opcodeByte := 57
      handlerBody := "CodeCopyOpcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .copyWords
      memoryAccess := .copyDestination
      exitKind := .continue
      stackInputs := 3
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .returndatacopy =>
    {
      opcodeByte := 62
      handlerBody := "ReturnDataCopyOpcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .copyWords
      memoryAccess := .copyDestination
      exitKind := .continue
      stackInputs := 3
      stackGrowth := 0
      immediateWidth := 0
      activation := .eip211
    }
  | .pop =>
    {
      opcodeByte := 80
      handlerBody := "PopOpcode"
      handlerRoot := .ordinary
      gasClass := .base
      fixedGas := 2
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 1
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .mload =>
    {
      opcodeByte := 81
      handlerBody := "MLoadOpcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .word32
      exitKind := .continue
      stackInputs := 1
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .mstore =>
    {
      opcodeByte := 82
      handlerBody := "MStoreOpcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .word32
      exitKind := .continue
      stackInputs := 2
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .mstore8 =>
    {
      opcodeByte := 83
      handlerBody := "MStore8Opcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .byte1
      exitKind := .continue
      stackInputs := 2
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .jump =>
    {
      opcodeByte := 86
      handlerBody := "JumpOpcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .mid
      fixedGas := 8
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .jump
      stackInputs := 1
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .jumpi =>
    {
      opcodeByte := 87
      handlerBody := "JumpIfOpcode"
      handlerRoot := .jumpIf
      gasClass := .high
      fixedGas := 10
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .conditionalJump
      stackInputs := 2
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .pc =>
    {
      opcodeByte := 88
      handlerBody := "ProgramCounterOpcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .base
      fixedGas := 2
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .msize =>
    {
      opcodeByte := 89
      handlerBody := "EnvUInt64Opcode<EvmInstructions.OpMSize<TGasPolicy>,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .base
      fixedGas := 2
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .gas =>
    {
      opcodeByte := 90
      handlerBody := "GasOpcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .base
      fixedGas := 2
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .jumpdest =>
    {
      opcodeByte := 91
      handlerBody := "JumpDestOpcode"
      handlerRoot := .ordinary
      gasClass := .jumpdest
      fixedGas := 1
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .mcopy =>
    {
      opcodeByte := 94
      handlerBody := "MCopyOpcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .copyWords
      memoryAccess := .memoryCopy
      exitKind := .continue
      stackInputs := 3
      stackGrowth := 0
      immediateWidth := 0
      activation := .eip5656
    }
  | .push0 =>
    {
      opcodeByte := 95
      handlerBody := "Push0Opcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .base
      fixedGas := 2
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 0
      activation := .eip3855
    }
  | .push1 =>
    {
      opcodeByte := 96
      handlerBody := "PushOpcode<EvmInstructions.Op1,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 1
      activation := .unconditional
    }
  | .push2 =>
    {
      opcodeByte := 97
      handlerBody := "Push2Opcode<TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 2
      activation := .unconditional
    }
  | .push3 =>
    {
      opcodeByte := 98
      handlerBody := "PushOpcode<EvmInstructions.Op3,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 3
      activation := .unconditional
    }
  | .push4 =>
    {
      opcodeByte := 99
      handlerBody := "PushOpcode<EvmInstructions.Op4,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 4
      activation := .unconditional
    }
  | .push5 =>
    {
      opcodeByte := 100
      handlerBody := "PushOpcode<EvmInstructions.Op5,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 5
      activation := .unconditional
    }
  | .push6 =>
    {
      opcodeByte := 101
      handlerBody := "PushOpcode<EvmInstructions.Op6,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 6
      activation := .unconditional
    }
  | .push7 =>
    {
      opcodeByte := 102
      handlerBody := "PushOpcode<EvmInstructions.Op7,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 7
      activation := .unconditional
    }
  | .push8 =>
    {
      opcodeByte := 103
      handlerBody := "PushOpcode<EvmInstructions.Op8,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 8
      activation := .unconditional
    }
  | .push9 =>
    {
      opcodeByte := 104
      handlerBody := "PushOpcode<EvmInstructions.Op9,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 9
      activation := .unconditional
    }
  | .push10 =>
    {
      opcodeByte := 105
      handlerBody := "PushOpcode<EvmInstructions.Op10,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 10
      activation := .unconditional
    }
  | .push11 =>
    {
      opcodeByte := 106
      handlerBody := "PushOpcode<EvmInstructions.Op11,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 11
      activation := .unconditional
    }
  | .push12 =>
    {
      opcodeByte := 107
      handlerBody := "PushOpcode<EvmInstructions.Op12,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 12
      activation := .unconditional
    }
  | .push13 =>
    {
      opcodeByte := 108
      handlerBody := "PushOpcode<EvmInstructions.Op13,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 13
      activation := .unconditional
    }
  | .push14 =>
    {
      opcodeByte := 109
      handlerBody := "PushOpcode<EvmInstructions.Op14,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 14
      activation := .unconditional
    }
  | .push15 =>
    {
      opcodeByte := 110
      handlerBody := "PushOpcode<EvmInstructions.Op15,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 15
      activation := .unconditional
    }
  | .push16 =>
    {
      opcodeByte := 111
      handlerBody := "PushOpcode<EvmInstructions.Op16,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 16
      activation := .unconditional
    }
  | .push17 =>
    {
      opcodeByte := 112
      handlerBody := "PushOpcode<EvmInstructions.Op17,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 17
      activation := .unconditional
    }
  | .push18 =>
    {
      opcodeByte := 113
      handlerBody := "PushOpcode<EvmInstructions.Op18,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 18
      activation := .unconditional
    }
  | .push19 =>
    {
      opcodeByte := 114
      handlerBody := "PushOpcode<EvmInstructions.Op19,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 19
      activation := .unconditional
    }
  | .push20 =>
    {
      opcodeByte := 115
      handlerBody := "PushOpcode<EvmInstructions.Op20,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 20
      activation := .unconditional
    }
  | .push21 =>
    {
      opcodeByte := 116
      handlerBody := "PushOpcode<EvmInstructions.Op21,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 21
      activation := .unconditional
    }
  | .push22 =>
    {
      opcodeByte := 117
      handlerBody := "PushOpcode<EvmInstructions.Op22,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 22
      activation := .unconditional
    }
  | .push23 =>
    {
      opcodeByte := 118
      handlerBody := "PushOpcode<EvmInstructions.Op23,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 23
      activation := .unconditional
    }
  | .push24 =>
    {
      opcodeByte := 119
      handlerBody := "PushOpcode<EvmInstructions.Op24,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 24
      activation := .unconditional
    }
  | .push25 =>
    {
      opcodeByte := 120
      handlerBody := "PushOpcode<EvmInstructions.Op25,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 25
      activation := .unconditional
    }
  | .push26 =>
    {
      opcodeByte := 121
      handlerBody := "PushOpcode<EvmInstructions.Op26,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 26
      activation := .unconditional
    }
  | .push27 =>
    {
      opcodeByte := 122
      handlerBody := "PushOpcode<EvmInstructions.Op27,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 27
      activation := .unconditional
    }
  | .push28 =>
    {
      opcodeByte := 123
      handlerBody := "PushOpcode<EvmInstructions.Op28,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 28
      activation := .unconditional
    }
  | .push29 =>
    {
      opcodeByte := 124
      handlerBody := "PushOpcode<EvmInstructions.Op29,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 29
      activation := .unconditional
    }
  | .push30 =>
    {
      opcodeByte := 125
      handlerBody := "PushOpcode<EvmInstructions.Op30,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 30
      activation := .unconditional
    }
  | .push31 =>
    {
      opcodeByte := 126
      handlerBody := "PushOpcode<EvmInstructions.Op31,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 31
      activation := .unconditional
    }
  | .push32 =>
    {
      opcodeByte := 127
      handlerBody := "PushOpcode<EvmInstructions.Op32,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 0
      stackGrowth := 1
      immediateWidth := 32
      activation := .unconditional
    }
  | .dup1 =>
    {
      opcodeByte := 128
      handlerBody := "DupOpcode<EvmInstructions.Op1,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 1
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup2 =>
    {
      opcodeByte := 129
      handlerBody := "DupOpcode<EvmInstructions.Op2,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 2
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup3 =>
    {
      opcodeByte := 130
      handlerBody := "DupOpcode<EvmInstructions.Op3,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 3
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup4 =>
    {
      opcodeByte := 131
      handlerBody := "DupOpcode<EvmInstructions.Op4,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 4
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup5 =>
    {
      opcodeByte := 132
      handlerBody := "DupOpcode<EvmInstructions.Op5,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 5
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup6 =>
    {
      opcodeByte := 133
      handlerBody := "DupOpcode<EvmInstructions.Op6,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 6
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup7 =>
    {
      opcodeByte := 134
      handlerBody := "DupOpcode<EvmInstructions.Op7,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 7
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup8 =>
    {
      opcodeByte := 135
      handlerBody := "DupOpcode<EvmInstructions.Op8,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 8
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup9 =>
    {
      opcodeByte := 136
      handlerBody := "DupOpcode<EvmInstructions.Op9,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 9
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup10 =>
    {
      opcodeByte := 137
      handlerBody := "DupOpcode<EvmInstructions.Op10,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 10
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup11 =>
    {
      opcodeByte := 138
      handlerBody := "DupOpcode<EvmInstructions.Op11,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 11
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup12 =>
    {
      opcodeByte := 139
      handlerBody := "DupOpcode<EvmInstructions.Op12,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 12
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup13 =>
    {
      opcodeByte := 140
      handlerBody := "DupOpcode<EvmInstructions.Op13,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 13
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup14 =>
    {
      opcodeByte := 141
      handlerBody := "DupOpcode<EvmInstructions.Op14,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 14
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup15 =>
    {
      opcodeByte := 142
      handlerBody := "DupOpcode<EvmInstructions.Op15,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 15
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .dup16 =>
    {
      opcodeByte := 143
      handlerBody := "DupOpcode<EvmInstructions.Op16,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 16
      stackGrowth := 1
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap1 =>
    {
      opcodeByte := 144
      handlerBody := "SwapOpcode<EvmInstructions.Op1,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 2
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap2 =>
    {
      opcodeByte := 145
      handlerBody := "SwapOpcode<EvmInstructions.Op2,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 3
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap3 =>
    {
      opcodeByte := 146
      handlerBody := "SwapOpcode<EvmInstructions.Op3,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 4
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap4 =>
    {
      opcodeByte := 147
      handlerBody := "SwapOpcode<EvmInstructions.Op4,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 5
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap5 =>
    {
      opcodeByte := 148
      handlerBody := "SwapOpcode<EvmInstructions.Op5,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 6
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap6 =>
    {
      opcodeByte := 149
      handlerBody := "SwapOpcode<EvmInstructions.Op6,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 7
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap7 =>
    {
      opcodeByte := 150
      handlerBody := "SwapOpcode<EvmInstructions.Op7,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 8
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap8 =>
    {
      opcodeByte := 151
      handlerBody := "SwapOpcode<EvmInstructions.Op8,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 9
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap9 =>
    {
      opcodeByte := 152
      handlerBody := "SwapOpcode<EvmInstructions.Op9,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 10
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap10 =>
    {
      opcodeByte := 153
      handlerBody := "SwapOpcode<EvmInstructions.Op10,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 11
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap11 =>
    {
      opcodeByte := 154
      handlerBody := "SwapOpcode<EvmInstructions.Op11,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 12
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap12 =>
    {
      opcodeByte := 155
      handlerBody := "SwapOpcode<EvmInstructions.Op12,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 13
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap13 =>
    {
      opcodeByte := 156
      handlerBody := "SwapOpcode<EvmInstructions.Op13,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 14
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap14 =>
    {
      opcodeByte := 157
      handlerBody := "SwapOpcode<EvmInstructions.Op14,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 15
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap15 =>
    {
      opcodeByte := 158
      handlerBody := "SwapOpcode<EvmInstructions.Op15,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 16
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .swap16 =>
    {
      opcodeByte := 159
      handlerBody := "SwapOpcode<EvmInstructions.Op16,TTracingInst>"
      handlerRoot := .ordinary
      gasClass := .veryLow
      fixedGas := 3
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .continue
      stackInputs := 17
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .return =>
    {
      opcodeByte := 243
      handlerBody := "ReturnOpcode"
      handlerRoot := .terminating
      gasClass := .zero
      fixedGas := 0
      dynamicGas := .none
      memoryAccess := .returnRange
      exitKind := .returnData
      stackInputs := 2
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }
  | .revert =>
    {
      opcodeByte := 253
      handlerBody := "RevertOpcode"
      handlerRoot := .terminating
      gasClass := .zero
      fixedGas := 0
      dynamicGas := .none
      memoryAccess := .returnRange
      exitKind := .revertData
      stackInputs := 2
      stackGrowth := 0
      immediateWidth := 0
      activation := .eip140
    }
  | .invalid =>
    {
      opcodeByte := 254
      handlerBody := "InvalidOpcode"
      handlerRoot := .terminating
      gasClass := .zero
      fixedGas := 0
      dynamicGas := .none
      memoryAccess := .none
      exitKind := .invalid
      stackInputs := 0
      stackGrowth := 0
      immediateWidth := 0
      activation := .unconditional
    }

def copyWordGas : Nat := 3

def memoryLinearGas : Nat := 3

def activeOnAmsterdam (_opcode : Opcode) : Bool := true

def tracingFlag : DispatchTable → String
  | .noTrace | .noTraceCancelable => "OffFlag"
  | .traced | .tracedCancelable => "OnFlag"

def cancelableFlag : DispatchTable → String
  | .noTrace | .traced => "OffFlag"
  | .noTraceCancelable | .tracedCancelable => "OnFlag"

def closedHandlerBody (opcode : Opcode) (table : DispatchTable) : String :=
  ((descriptor opcode).handlerBody.replace "TTracingInst" (tracingFlag table)).replace
    "TGasPolicy" "EthereumGasPolicy"

def expectedClosedRoot (opcode : Opcode) (table : DispatchTable) : String :=
  match (descriptor opcode).handlerRoot with
  | .ordinary => "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<" ++ closedHandlerBody opcode table ++ "," ++
      tracingFlag table ++ "," ++ cancelableFlag table ++ ",OnFlag>"
  | .terminating => "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<" ++ closedHandlerBody opcode table ++ "," ++
      tracingFlag table ++ "," ++ cancelableFlag table ++ ",OffFlag>"
  | .jumpIf => "VirtualMachine<EthereumGasPolicy>.ExecuteJumpIfOpcode<" ++ tracingFlag table ++ "," ++
      cancelableFlag table ++ ">"

def specializations : List Specialization :=
  [
    { opcode := .stop, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<StopOpcode,OffFlag,OffFlag,OffFlag>" },
    { opcode := .stop, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<StopOpcode,OffFlag,OnFlag,OffFlag>" },
    { opcode := .stop, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<StopOpcode,OnFlag,OffFlag,OffFlag>" },
    { opcode := .stop, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<StopOpcode,OnFlag,OnFlag,OffFlag>" },
    { opcode := .calldataload, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CallDataLoadOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .calldataload, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CallDataLoadOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .calldataload, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CallDataLoadOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .calldataload, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CallDataLoadOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .calldatacopy, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CallDataCopyOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .calldatacopy, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CallDataCopyOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .calldatacopy, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CallDataCopyOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .calldatacopy, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CallDataCopyOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .codecopy, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CodeCopyOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .codecopy, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CodeCopyOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .codecopy, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CodeCopyOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .codecopy, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<CodeCopyOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .returndatacopy, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ReturnDataCopyOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .returndatacopy, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ReturnDataCopyOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .returndatacopy, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ReturnDataCopyOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .returndatacopy, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ReturnDataCopyOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .pop, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PopOpcode,OffFlag,OffFlag,OnFlag>" },
    { opcode := .pop, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PopOpcode,OffFlag,OnFlag,OnFlag>" },
    { opcode := .pop, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PopOpcode,OnFlag,OffFlag,OnFlag>" },
    { opcode := .pop, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PopOpcode,OnFlag,OnFlag,OnFlag>" },
    { opcode := .mload, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MLoadOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .mload, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MLoadOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .mload, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MLoadOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .mload, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MLoadOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .mstore, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MStoreOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .mstore, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MStoreOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .mstore, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MStoreOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .mstore, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MStoreOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .mstore8, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MStore8Opcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .mstore8, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MStore8Opcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .mstore8, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MStore8Opcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .mstore8, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MStore8Opcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .jump, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<JumpOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .jump, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<JumpOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .jump, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<JumpOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .jump, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<JumpOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .jumpi, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteJumpIfOpcode<OffFlag,OffFlag>" },
    { opcode := .jumpi, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteJumpIfOpcode<OffFlag,OnFlag>" },
    { opcode := .jumpi, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteJumpIfOpcode<OnFlag,OffFlag>" },
    { opcode := .jumpi, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteJumpIfOpcode<OnFlag,OnFlag>" },
    { opcode := .pc, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ProgramCounterOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .pc, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ProgramCounterOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .pc, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ProgramCounterOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .pc, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ProgramCounterOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .msize, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<EnvUInt64Opcode<EvmInstructions.OpMSize<EthereumGasPolicy>,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .msize, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<EnvUInt64Opcode<EvmInstructions.OpMSize<EthereumGasPolicy>,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .msize, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<EnvUInt64Opcode<EvmInstructions.OpMSize<EthereumGasPolicy>,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .msize, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<EnvUInt64Opcode<EvmInstructions.OpMSize<EthereumGasPolicy>,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .gas, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<GasOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .gas, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<GasOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .gas, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<GasOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .gas, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<GasOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .jumpdest, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<JumpDestOpcode,OffFlag,OffFlag,OnFlag>" },
    { opcode := .jumpdest, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<JumpDestOpcode,OffFlag,OnFlag,OnFlag>" },
    { opcode := .jumpdest, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<JumpDestOpcode,OnFlag,OffFlag,OnFlag>" },
    { opcode := .jumpdest, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<JumpDestOpcode,OnFlag,OnFlag,OnFlag>" },
    { opcode := .mcopy, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MCopyOpcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .mcopy, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MCopyOpcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .mcopy, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MCopyOpcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .mcopy, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<MCopyOpcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push0, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<Push0Opcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push0, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<Push0Opcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push0, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<Push0Opcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push0, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<Push0Opcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push1, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op1,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push1, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op1,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push1, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op1,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push1, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op1,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push2, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<Push2Opcode<OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push2, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<Push2Opcode<OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push2, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<Push2Opcode<OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push2, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<Push2Opcode<OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push3, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op3,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push3, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op3,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push3, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op3,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push3, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op3,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push4, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op4,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push4, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op4,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push4, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op4,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push4, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op4,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push5, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op5,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push5, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op5,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push5, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op5,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push5, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op5,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push6, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op6,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push6, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op6,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push6, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op6,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push6, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op6,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push7, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op7,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push7, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op7,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push7, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op7,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push7, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op7,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push8, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op8,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push8, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op8,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push8, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op8,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push8, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op8,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push9, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op9,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push9, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op9,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push9, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op9,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push9, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op9,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push10, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op10,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push10, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op10,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push10, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op10,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push10, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op10,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push11, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op11,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push11, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op11,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push11, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op11,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push11, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op11,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push12, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op12,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push12, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op12,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push12, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op12,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push12, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op12,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push13, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op13,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push13, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op13,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push13, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op13,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push13, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op13,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push14, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op14,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push14, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op14,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push14, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op14,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push14, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op14,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push15, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op15,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push15, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op15,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push15, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op15,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push15, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op15,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push16, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op16,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push16, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op16,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push16, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op16,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push16, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op16,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push17, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op17,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push17, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op17,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push17, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op17,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push17, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op17,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push18, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op18,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push18, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op18,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push18, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op18,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push18, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op18,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push19, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op19,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push19, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op19,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push19, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op19,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push19, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op19,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push20, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op20,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push20, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op20,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push20, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op20,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push20, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op20,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push21, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op21,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push21, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op21,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push21, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op21,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push21, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op21,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push22, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op22,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push22, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op22,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push22, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op22,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push22, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op22,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push23, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op23,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push23, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op23,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push23, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op23,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push23, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op23,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push24, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op24,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push24, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op24,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push24, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op24,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push24, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op24,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push25, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op25,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push25, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op25,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push25, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op25,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push25, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op25,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push26, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op26,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push26, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op26,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push26, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op26,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push26, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op26,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push27, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op27,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push27, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op27,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push27, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op27,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push27, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op27,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push28, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op28,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push28, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op28,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push28, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op28,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push28, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op28,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push29, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op29,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push29, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op29,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push29, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op29,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push29, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op29,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push30, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op30,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push30, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op30,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push30, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op30,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push30, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op30,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push31, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op31,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push31, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op31,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push31, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op31,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push31, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op31,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .push32, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op32,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .push32, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op32,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .push32, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op32,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .push32, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PushOpcode<EvmInstructions.Op32,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup1, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op1,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup1, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op1,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup1, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op1,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup1, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op1,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup2, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op2,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup2, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op2,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup2, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op2,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup2, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op2,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup3, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op3,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup3, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op3,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup3, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op3,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup3, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op3,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup4, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op4,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup4, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op4,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup4, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op4,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup4, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op4,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup5, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op5,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup5, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op5,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup5, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op5,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup5, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op5,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup6, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op6,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup6, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op6,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup6, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op6,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup6, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op6,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup7, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op7,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup7, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op7,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup7, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op7,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup7, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op7,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup8, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op8,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup8, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op8,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup8, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op8,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup8, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op8,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup9, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op9,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup9, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op9,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup9, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op9,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup9, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op9,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup10, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op10,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup10, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op10,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup10, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op10,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup10, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op10,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup11, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op11,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup11, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op11,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup11, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op11,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup11, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op11,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup12, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op12,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup12, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op12,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup12, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op12,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup12, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op12,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup13, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op13,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup13, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op13,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup13, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op13,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup13, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op13,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup14, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op14,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup14, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op14,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup14, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op14,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup14, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op14,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup15, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op15,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup15, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op15,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup15, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op15,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup15, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op15,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .dup16, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op16,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .dup16, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op16,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .dup16, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op16,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .dup16, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op16,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap1, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op1,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap1, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op1,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap1, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op1,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap1, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op1,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap2, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op2,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap2, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op2,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap2, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op2,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap2, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op2,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap3, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op3,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap3, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op3,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap3, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op3,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap3, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op3,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap4, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op4,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap4, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op4,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap4, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op4,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap4, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op4,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap5, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op5,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap5, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op5,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap5, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op5,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap5, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op5,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap6, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op6,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap6, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op6,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap6, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op6,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap6, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op6,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap7, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op7,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap7, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op7,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap7, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op7,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap7, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op7,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap8, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op8,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap8, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op8,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap8, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op8,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap8, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op8,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap9, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op9,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap9, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op9,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap9, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op9,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap9, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op9,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap10, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op10,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap10, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op10,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap10, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op10,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap10, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op10,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap11, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op11,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap11, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op11,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap11, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op11,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap11, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op11,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap12, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op12,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap12, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op12,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap12, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op12,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap12, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op12,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap13, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op13,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap13, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op13,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap13, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op13,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap13, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op13,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap14, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op14,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap14, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op14,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap14, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op14,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap14, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op14,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap15, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op15,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap15, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op15,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap15, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op15,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap15, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op15,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .swap16, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op16,OffFlag>,OffFlag,OffFlag,OnFlag>" },
    { opcode := .swap16, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op16,OffFlag>,OffFlag,OnFlag,OnFlag>" },
    { opcode := .swap16, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op16,OnFlag>,OnFlag,OffFlag,OnFlag>" },
    { opcode := .swap16, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op16,OnFlag>,OnFlag,OnFlag,OnFlag>" },
    { opcode := .return, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ReturnOpcode,OffFlag,OffFlag,OffFlag>" },
    { opcode := .return, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ReturnOpcode,OffFlag,OnFlag,OffFlag>" },
    { opcode := .return, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ReturnOpcode,OnFlag,OffFlag,OffFlag>" },
    { opcode := .return, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<ReturnOpcode,OnFlag,OnFlag,OffFlag>" },
    { opcode := .revert, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<RevertOpcode,OffFlag,OffFlag,OffFlag>" },
    { opcode := .revert, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<RevertOpcode,OffFlag,OnFlag,OffFlag>" },
    { opcode := .revert, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<RevertOpcode,OnFlag,OffFlag,OffFlag>" },
    { opcode := .revert, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<RevertOpcode,OnFlag,OnFlag,OffFlag>" },
    { opcode := .invalid, table := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<InvalidOpcode,OffFlag,OffFlag,OffFlag>" },
    { opcode := .invalid, table := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<InvalidOpcode,OffFlag,OnFlag,OffFlag>" },
    { opcode := .invalid, table := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<InvalidOpcode,OnFlag,OffFlag,OffFlag>" },
    { opcode := .invalid, table := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<InvalidOpcode,OnFlag,OnFlag,OffFlag>" }
  ]

def forkLineage : List String :=
  [
    "Olympic",
    "Frontier",
    "Homestead",
    "Dao",
    "TangerineWhistle",
    "SpuriousDragon",
    "Byzantium",
    "Constantinople",
    "ConstantinopleFix",
    "Istanbul",
    "MuirGlacier",
    "Berlin",
    "London",
    "ArrowGlacier",
    "GrayGlacier",
    "Paris",
    "Shanghai",
    "Cancun",
    "Prague",
    "Osaka",
    "BPO1",
    "BPO2",
    "Amsterdam"
  ]

end Eip803x.Generated.MemoryControlOpcodeKernel
