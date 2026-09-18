-- SPDX-FileCopyrightText: 2026 Demerzel Solutions Limited
-- SPDX-License-Identifier: LGPL-3.0-only

-- This file is generated. Do not edit.
-- Extractor version: 1.0.0
-- Production closure SHA-256: b3e3a2f507e9d9673a599ce38df42e147fac7bb01003bff100996284ecc055f7
-- Canonical IR SHA-256: aa0052d451b67f6b1dda11cc0c04bb5cf90558a4449bf38dcc3940ae0089d55c

import Eip803x.Gas
import Eip803x.Evm.MemoryStackControl
import Eip803x.Evm.Word

namespace Eip803x.Generated.StackRearrangementOpcodeKernel

open Eip803x
open Eip803x.Evm
open Eip803x.Evm.MemoryStackControl

def schemaVersion : Nat := 1
def extractorVersion : String := "1.0.0"
def kernel : String := "StackRearrangementOpcodeKernel"
def root : String := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>"
def gasPolicy : String := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
def fork : String := "Nethermind.Specs.Forks.Amsterdam"
def buildFlavor : String := "standard-mainnet"
def diClosure : String := "EthereumVirtualMachine -> VirtualMachine<EthereumGasPolicy> -> IVirtualMachine"
def forkClosure : String := "Olympic -> ... -> BPO1 -> BPO2 -> Amsterdam"
def pcOrder : String := "pc-before-gas"
def gasOrder : String := "gas-before-stack"
def faultOrder : String := "out-of-gas-before-underflow-before-overflow"
def stackLimit : Nat := 1024
def popGas : Nat := 2
def rearrangementGas : Nat := 3
def claimHeader : List (String × String) :=
  [("extractorVersion", extractorVersion), ("kernel", kernel), ("root", root),
   ("gasPolicy", gasPolicy), ("fork", fork), ("buildFlavor", buildFlavor),
   ("diClosure", diClosure), ("forkClosure", forkClosure), ("pcOrder", pcOrder),
   ("gasOrder", gasOrder), ("faultOrder", faultOrder)]
def claimNumbers : List Nat := [schemaVersion, stackLimit, popGas, rearrangementGas]

inductive Opcode where
  | pop
  | dup1
  | dup2
  | dup3
  | dup4
  | dup5
  | dup6
  | dup7
  | dup8
  | dup9
  | dup10
  | dup11
  | dup12
  | dup13
  | dup14
  | dup15
  | dup16
  | swap1
  | swap2
  | swap3
  | swap4
  | swap5
  | swap6
  | swap7
  | swap8
  | swap9
  | swap10
  | swap11
  | swap12
  | swap13
  | swap14
  | swap15
  | swap16
  deriving DecidableEq, Repr

def allOpcodes : List Opcode :=
  [
    .pop,
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
    .swap16
  ]

inductive GasClass where
  | base
  | veryLow
  deriving DecidableEq, Repr

inductive StackEffect where
  | pop
  | duplicate
  | swap
  deriving DecidableEq, Repr

structure Descriptor where
  name : String
  instruction : String
  opcodeByte : Nat
  handlerBody : String
  handlerRoot : String
  dispatchAssignment : String
  gasClass : GasClass
  fixedGas : Nat
  stackInputs : Nat
  stackGrowth : Nat
  operandDepth : Nat
  stackEffect : StackEffect
  pcOrder : String
  gasOrder : String
  faultOrder : String
  activation : String
  fork : String
  gasPolicy : String
  vmRoot : String
  deriving DecidableEq, Repr

def descriptor : Opcode → Descriptor
  | .pop => {
      name := "pop"
      instruction := "POP"
      opcodeByte := 80
      handlerBody := "PopOpcode"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.POP]=OpcodeHandler<PopOpcode,TTracingInst,TCancelable>();"
      gasClass := .base
      fixedGas := 2
      stackInputs := 1
      stackGrowth := 0
      operandDepth := 0
      stackEffect := .pop
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup1 => {
      name := "dup1"
      instruction := "DUP1"
      opcodeByte := 128
      handlerBody := "DupOpcode<EvmInstructions.Op1,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP1]=OpcodeHandler<DupOpcode<EvmInstructions.Op1,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 1
      stackGrowth := 1
      operandDepth := 1
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup2 => {
      name := "dup2"
      instruction := "DUP2"
      opcodeByte := 129
      handlerBody := "DupOpcode<EvmInstructions.Op2,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP2]=OpcodeHandler<DupOpcode<EvmInstructions.Op2,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 2
      stackGrowth := 1
      operandDepth := 2
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup3 => {
      name := "dup3"
      instruction := "DUP3"
      opcodeByte := 130
      handlerBody := "DupOpcode<EvmInstructions.Op3,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP3]=OpcodeHandler<DupOpcode<EvmInstructions.Op3,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 3
      stackGrowth := 1
      operandDepth := 3
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup4 => {
      name := "dup4"
      instruction := "DUP4"
      opcodeByte := 131
      handlerBody := "DupOpcode<EvmInstructions.Op4,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP4]=OpcodeHandler<DupOpcode<EvmInstructions.Op4,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 4
      stackGrowth := 1
      operandDepth := 4
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup5 => {
      name := "dup5"
      instruction := "DUP5"
      opcodeByte := 132
      handlerBody := "DupOpcode<EvmInstructions.Op5,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP5]=OpcodeHandler<DupOpcode<EvmInstructions.Op5,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 5
      stackGrowth := 1
      operandDepth := 5
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup6 => {
      name := "dup6"
      instruction := "DUP6"
      opcodeByte := 133
      handlerBody := "DupOpcode<EvmInstructions.Op6,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP6]=OpcodeHandler<DupOpcode<EvmInstructions.Op6,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 6
      stackGrowth := 1
      operandDepth := 6
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup7 => {
      name := "dup7"
      instruction := "DUP7"
      opcodeByte := 134
      handlerBody := "DupOpcode<EvmInstructions.Op7,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP7]=OpcodeHandler<DupOpcode<EvmInstructions.Op7,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 7
      stackGrowth := 1
      operandDepth := 7
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup8 => {
      name := "dup8"
      instruction := "DUP8"
      opcodeByte := 135
      handlerBody := "DupOpcode<EvmInstructions.Op8,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP8]=OpcodeHandler<DupOpcode<EvmInstructions.Op8,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 8
      stackGrowth := 1
      operandDepth := 8
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup9 => {
      name := "dup9"
      instruction := "DUP9"
      opcodeByte := 136
      handlerBody := "DupOpcode<EvmInstructions.Op9,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP9]=OpcodeHandler<DupOpcode<EvmInstructions.Op9,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 9
      stackGrowth := 1
      operandDepth := 9
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup10 => {
      name := "dup10"
      instruction := "DUP10"
      opcodeByte := 137
      handlerBody := "DupOpcode<EvmInstructions.Op10,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP10]=OpcodeHandler<DupOpcode<EvmInstructions.Op10,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 10
      stackGrowth := 1
      operandDepth := 10
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup11 => {
      name := "dup11"
      instruction := "DUP11"
      opcodeByte := 138
      handlerBody := "DupOpcode<EvmInstructions.Op11,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP11]=OpcodeHandler<DupOpcode<EvmInstructions.Op11,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 11
      stackGrowth := 1
      operandDepth := 11
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup12 => {
      name := "dup12"
      instruction := "DUP12"
      opcodeByte := 139
      handlerBody := "DupOpcode<EvmInstructions.Op12,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP12]=OpcodeHandler<DupOpcode<EvmInstructions.Op12,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 12
      stackGrowth := 1
      operandDepth := 12
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup13 => {
      name := "dup13"
      instruction := "DUP13"
      opcodeByte := 140
      handlerBody := "DupOpcode<EvmInstructions.Op13,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP13]=OpcodeHandler<DupOpcode<EvmInstructions.Op13,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 13
      stackGrowth := 1
      operandDepth := 13
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup14 => {
      name := "dup14"
      instruction := "DUP14"
      opcodeByte := 141
      handlerBody := "DupOpcode<EvmInstructions.Op14,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP14]=OpcodeHandler<DupOpcode<EvmInstructions.Op14,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 14
      stackGrowth := 1
      operandDepth := 14
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup15 => {
      name := "dup15"
      instruction := "DUP15"
      opcodeByte := 142
      handlerBody := "DupOpcode<EvmInstructions.Op15,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP15]=OpcodeHandler<DupOpcode<EvmInstructions.Op15,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 15
      stackGrowth := 1
      operandDepth := 15
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .dup16 => {
      name := "dup16"
      instruction := "DUP16"
      opcodeByte := 143
      handlerBody := "DupOpcode<EvmInstructions.Op16,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.DUP16]=OpcodeHandler<DupOpcode<EvmInstructions.Op16,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 16
      stackGrowth := 1
      operandDepth := 16
      stackEffect := .duplicate
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap1 => {
      name := "swap1"
      instruction := "SWAP1"
      opcodeByte := 144
      handlerBody := "SwapOpcode<EvmInstructions.Op1,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP1]=OpcodeHandler<SwapOpcode<EvmInstructions.Op1,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 2
      stackGrowth := 0
      operandDepth := 1
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap2 => {
      name := "swap2"
      instruction := "SWAP2"
      opcodeByte := 145
      handlerBody := "SwapOpcode<EvmInstructions.Op2,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP2]=OpcodeHandler<SwapOpcode<EvmInstructions.Op2,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 3
      stackGrowth := 0
      operandDepth := 2
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap3 => {
      name := "swap3"
      instruction := "SWAP3"
      opcodeByte := 146
      handlerBody := "SwapOpcode<EvmInstructions.Op3,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP3]=OpcodeHandler<SwapOpcode<EvmInstructions.Op3,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 4
      stackGrowth := 0
      operandDepth := 3
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap4 => {
      name := "swap4"
      instruction := "SWAP4"
      opcodeByte := 147
      handlerBody := "SwapOpcode<EvmInstructions.Op4,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP4]=OpcodeHandler<SwapOpcode<EvmInstructions.Op4,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 5
      stackGrowth := 0
      operandDepth := 4
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap5 => {
      name := "swap5"
      instruction := "SWAP5"
      opcodeByte := 148
      handlerBody := "SwapOpcode<EvmInstructions.Op5,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP5]=OpcodeHandler<SwapOpcode<EvmInstructions.Op5,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 6
      stackGrowth := 0
      operandDepth := 5
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap6 => {
      name := "swap6"
      instruction := "SWAP6"
      opcodeByte := 149
      handlerBody := "SwapOpcode<EvmInstructions.Op6,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP6]=OpcodeHandler<SwapOpcode<EvmInstructions.Op6,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 7
      stackGrowth := 0
      operandDepth := 6
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap7 => {
      name := "swap7"
      instruction := "SWAP7"
      opcodeByte := 150
      handlerBody := "SwapOpcode<EvmInstructions.Op7,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP7]=OpcodeHandler<SwapOpcode<EvmInstructions.Op7,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 8
      stackGrowth := 0
      operandDepth := 7
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap8 => {
      name := "swap8"
      instruction := "SWAP8"
      opcodeByte := 151
      handlerBody := "SwapOpcode<EvmInstructions.Op8,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP8]=OpcodeHandler<SwapOpcode<EvmInstructions.Op8,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 9
      stackGrowth := 0
      operandDepth := 8
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap9 => {
      name := "swap9"
      instruction := "SWAP9"
      opcodeByte := 152
      handlerBody := "SwapOpcode<EvmInstructions.Op9,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP9]=OpcodeHandler<SwapOpcode<EvmInstructions.Op9,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 10
      stackGrowth := 0
      operandDepth := 9
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap10 => {
      name := "swap10"
      instruction := "SWAP10"
      opcodeByte := 153
      handlerBody := "SwapOpcode<EvmInstructions.Op10,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP10]=OpcodeHandler<SwapOpcode<EvmInstructions.Op10,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 11
      stackGrowth := 0
      operandDepth := 10
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap11 => {
      name := "swap11"
      instruction := "SWAP11"
      opcodeByte := 154
      handlerBody := "SwapOpcode<EvmInstructions.Op11,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP11]=OpcodeHandler<SwapOpcode<EvmInstructions.Op11,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 12
      stackGrowth := 0
      operandDepth := 11
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap12 => {
      name := "swap12"
      instruction := "SWAP12"
      opcodeByte := 155
      handlerBody := "SwapOpcode<EvmInstructions.Op12,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP12]=OpcodeHandler<SwapOpcode<EvmInstructions.Op12,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 13
      stackGrowth := 0
      operandDepth := 12
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap13 => {
      name := "swap13"
      instruction := "SWAP13"
      opcodeByte := 156
      handlerBody := "SwapOpcode<EvmInstructions.Op13,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP13]=OpcodeHandler<SwapOpcode<EvmInstructions.Op13,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 14
      stackGrowth := 0
      operandDepth := 13
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap14 => {
      name := "swap14"
      instruction := "SWAP14"
      opcodeByte := 157
      handlerBody := "SwapOpcode<EvmInstructions.Op14,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP14]=OpcodeHandler<SwapOpcode<EvmInstructions.Op14,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 15
      stackGrowth := 0
      operandDepth := 14
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap15 => {
      name := "swap15"
      instruction := "SWAP15"
      opcodeByte := 158
      handlerBody := "SwapOpcode<EvmInstructions.Op15,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP15]=OpcodeHandler<SwapOpcode<EvmInstructions.Op15,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 16
      stackGrowth := 0
      operandDepth := 15
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }
  | .swap16 => {
      name := "swap16"
      instruction := "SWAP16"
      opcodeByte := 159
      handlerBody := "SwapOpcode<EvmInstructions.Op16,TTracingInst>"
      handlerRoot := "ordinary"
      dispatchAssignment := "lookup[(int)Instruction.SWAP16]=OpcodeHandler<SwapOpcode<EvmInstructions.Op16,TTracingInst>,TTracingInst,TCancelable>();"
      gasClass := .veryLow
      fixedGas := 3
      stackInputs := 17
      stackGrowth := 0
      operandDepth := 16
      stackEffect := .swap
      pcOrder := "pc-before-gas"
      gasOrder := "gas-before-stack"
      faultOrder := "out-of-gas-before-underflow-before-overflow"
      activation := "Amsterdam"
      fork := "Nethermind.Specs.Forks.Amsterdam"
      gasPolicy := "Nethermind.Evm.GasPolicy.EthereumGasPolicy"
      vmRoot := "Nethermind.Evm.VirtualMachine<Nethermind.Evm.GasPolicy.EthereumGasPolicy>" }

def opcodeByte (opcode : Opcode) : Nat := (descriptor opcode).opcodeByte
def staticGas (opcode : Opcode) : Nat := (descriptor opcode).fixedGas
def stackInputs (opcode : Opcode) : Nat := (descriptor opcode).stackInputs
def stackGrowth (opcode : Opcode) : Nat := (descriptor opcode).stackGrowth
def operandDepth (opcode : Opcode) : Nat := (descriptor opcode).operandDepth
def stackEffect (opcode : Opcode) : StackEffect := (descriptor opcode).stackEffect

def descriptorText (opcode : Opcode) : List String :=
  let d := descriptor opcode
  [d.name, d.instruction, d.handlerBody, d.handlerRoot, d.dispatchAssignment, d.pcOrder,
   d.gasOrder, d.faultOrder, d.activation, d.fork, d.gasPolicy, d.vmRoot]
def descriptorNumbers (opcode : Opcode) : List Nat :=
  let d := descriptor opcode
  [d.opcodeByte, d.fixedGas, d.stackInputs, d.stackGrowth, d.operandDepth]
def descriptorGasClasses : List GasClass := allOpcodes.map fun opcode => (descriptor opcode).gasClass
def descriptorStackEffects : List StackEffect := allOpcodes.map fun opcode => (descriptor opcode).stackEffect

inductive DispatchTable where
  | noTrace
  | noTraceCancelable
  | traced
  | tracedCancelable
  deriving DecidableEq, Repr

structure Specialization where
  opcode : Opcode
  dispatchTable : DispatchTable
  tracingFlag : String
  cancelableFlag : String
  continuableFlag : String
  closedRoot : String
  handlerRoot : String
  deriving DecidableEq, Repr

def dispatchTables : List (String × String × String) :=
  [
    ("NoTrace", "OffFlag", "OffFlag"),
    ("NoTraceCancelable", "OffFlag", "OnFlag"),
    ("Traced", "OnFlag", "OffFlag"),
    ("TracedCancelable", "OnFlag", "OnFlag")
  ]

def specializations : List Specialization :=
  [
    { opcode := .pop, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PopOpcode,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .pop, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PopOpcode,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .pop, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PopOpcode,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .pop, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<PopOpcode,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup1, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op1,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup1, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op1,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup1, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op1,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup1, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op1,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup2, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op2,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup2, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op2,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup2, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op2,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup2, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op2,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup3, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op3,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup3, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op3,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup3, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op3,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup3, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op3,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup4, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op4,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup4, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op4,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup4, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op4,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup4, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op4,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup5, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op5,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup5, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op5,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup5, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op5,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup5, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op5,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup6, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op6,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup6, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op6,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup6, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op6,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup6, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op6,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup7, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op7,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup7, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op7,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup7, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op7,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup7, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op7,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup8, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op8,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup8, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op8,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup8, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op8,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup8, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op8,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup9, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op9,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup9, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op9,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup9, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op9,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup9, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op9,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup10, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op10,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup10, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op10,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup10, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op10,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup10, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op10,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup11, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op11,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup11, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op11,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup11, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op11,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup11, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op11,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup12, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op12,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup12, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op12,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup12, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op12,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup12, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op12,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup13, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op13,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup13, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op13,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup13, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op13,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup13, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op13,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup14, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op14,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup14, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op14,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup14, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op14,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup14, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op14,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup15, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op15,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup15, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op15,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup15, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op15,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup15, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op15,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup16, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op16,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup16, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op16,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup16, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op16,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .dup16, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<DupOpcode<EvmInstructions.Op16,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap1, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op1,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap1, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op1,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap1, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op1,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap1, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op1,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap2, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op2,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap2, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op2,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap2, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op2,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap2, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op2,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap3, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op3,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap3, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op3,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap3, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op3,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap3, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op3,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap4, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op4,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap4, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op4,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap4, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op4,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap4, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op4,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap5, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op5,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap5, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op5,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap5, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op5,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap5, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op5,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap6, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op6,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap6, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op6,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap6, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op6,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap6, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op6,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap7, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op7,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap7, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op7,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap7, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op7,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap7, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op7,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap8, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op8,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap8, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op8,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap8, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op8,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap8, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op8,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap9, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op9,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap9, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op9,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap9, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op9,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap9, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op9,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap10, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op10,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap10, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op10,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap10, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op10,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap10, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op10,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap11, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op11,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap11, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op11,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap11, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op11,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap11, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op11,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap12, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op12,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap12, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op12,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap12, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op12,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap12, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op12,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap13, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op13,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap13, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op13,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap13, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op13,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap13, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op13,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap14, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op14,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap14, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op14,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap14, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op14,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap14, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op14,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap15, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op15,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap15, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op15,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap15, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op15,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap15, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op15,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap16, dispatchTable := .noTrace, tracingFlag := "OffFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op16,OffFlag>,OffFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap16, dispatchTable := .noTraceCancelable, tracingFlag := "OffFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op16,OffFlag>,OffFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap16, dispatchTable := .traced, tracingFlag := "OnFlag", cancelableFlag := "OffFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op16,OnFlag>,OnFlag,OffFlag,OnFlag>", handlerRoot := "ordinary" },
    { opcode := .swap16, dispatchTable := .tracedCancelable, tracingFlag := "OnFlag", cancelableFlag := "OnFlag", continuableFlag := "OnFlag", closedRoot := "VirtualMachine<EthereumGasPolicy>.ExecuteOpcode<SwapOpcode<EvmInstructions.Op16,OnFlag>,OnFlag,OnFlag,OnFlag>", handlerRoot := "ordinary" },
  ]

def exactRootCount : Nat := specializations.length
def specializationOpcodes : List Opcode := specializations.map fun item => item.opcode
def specializationTables : List DispatchTable := specializations.map fun item => item.dispatchTable
def specializationTracingFlags : List String := specializations.map fun item => item.tracingFlag
def specializationCancelableFlags : List String := specializations.map fun item => item.cancelableFlag
def specializationContinuableFlags : List String := specializations.map fun item => item.continuableFlag
def specializationClosedRoots : List String := specializations.map fun item => item.closedRoot
def specializationHandlerRoots : List String := specializations.map fun item => item.handlerRoot

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
def openExtractionObligations : List String :=
  [
    "Roslyn admission and source routing do not prove the CLR, JIT, or unsafe EvmStack execution.",
    "The bounded reference models top-first UInt256 words and the 1024-word stack only; bytecode frames, memory, tracing, cancellation, and allocation limits remain provider obligations.",
    "The theorem-free generated machine consumes the admitted descriptor plan; its correspondence to the handwritten reference is proved only in the separate refinement, not by generated code.",
    "Exceptional-frame settlement, opcode counters, tail calls, trace callbacks, and transaction/block integration are outside this single-opcode projection."
  ]

inductive Operation where
  | pop
  | dup (depth : Nat)
  | swap (depth : Nat)
  deriving DecidableEq, Repr

structure Plan where
  fixedGas : Nat
  stackInputs : Nat
  stackGrowth : Nat
  stackLimit : Nat
  pcBeforeGas : Bool
  gasExhaustsOnFailure : Bool
  faultsAfterGas : Bool
  deriving DecidableEq, Repr

abbrev OperandStack := MemoryStackControl.Stack

structure State where
  stack : OperandStack
  gas : GasState
  pc : Nat
  deriving Repr

inductive Error where
  | outOfGas
  | stackUnderflow
  | stackOverflow
  deriving DecidableEq, Repr

inductive Outcome where
  | success (state : State)
  | error (reason : Error) (state : State)
  deriving Repr

namespace Outcome
def state : Outcome → State
  | .success state => state
  | .error _ state => state
def error? : Outcome → Option Error
  | .success _ => none
  | .error reason _ => some reason
end Outcome

namespace State
def withGasLeft (state : State) (gasLeft : Nat) : State :=
  { state with gas := { state.gas with gasLeft } }
def advancePc (state : State) : State :=
  { state with pc := state.pc + 1 }
end State

inductive Debit where
  | paid (state : State)
  | outOfGas (state : State)
  deriving Repr

def debit (plan : Plan) (state : State) : Debit :=
  if plan.fixedGas ≤ state.gas.gasLeft then
    .paid (state.withGasLeft (state.gas.gasLeft - plan.fixedGas))
  else
    .outOfGas (if plan.gasExhaustsOnFailure = true then state.withGasLeft 0 else state)

def readAt? {α : Type} : Nat → List α → Option α
  | _, [] => none
  | 0, head :: _ => some head
  | index + 1, _ :: tail => readAt? index tail

def replaceAt {α : Type} : Nat → α → List α → Option (List α)
  | _, _, [] => none
  | 0, value, _ :: tail => some (value :: tail)
  | index + 1, value, head :: tail =>
    (replaceAt index value tail).map (head :: ·)

def popWords : List UInt256 → Except Error (List UInt256)
  | [] => .error .stackUnderflow
  | _ :: tail => .ok tail

def duplicateWords (depth : Nat) (stack : List UInt256) : Except Error (List UInt256) :=
  if 0 < depth ∧ depth ≤ stack.length then
    match readAt? (depth - 1) stack with
    | none => .error .stackUnderflow
    | some value =>
      if stack.length < stackLimit then .ok (value :: stack) else .error .stackOverflow
  else
    .error .stackUnderflow

def swapWords (depth : Nat) (stack : List UInt256) : Except Error (List UInt256) :=
  match depth, stack with
  | 0, _ => .error .stackUnderflow
  | _, [] => .error .stackUnderflow
  | depth, top :: tail =>
    match readAt? (depth - 1) tail with
    | none => .error .stackUnderflow
    | some other =>
      match replaceAt (depth - 1) top tail with
      | none => .error .stackUnderflow
      | some changedTail => .ok (other :: changedTail)

def toOperation : Opcode → Operation
  | .pop => .pop
  | .dup1 => .dup 1
  | .dup2 => .dup 2
  | .dup3 => .dup 3
  | .dup4 => .dup 4
  | .dup5 => .dup 5
  | .dup6 => .dup 6
  | .dup7 => .dup 7
  | .dup8 => .dup 8
  | .dup9 => .dup 9
  | .dup10 => .dup 10
  | .dup11 => .dup 11
  | .dup12 => .dup 12
  | .dup13 => .dup 13
  | .dup14 => .dup 14
  | .dup15 => .dup 15
  | .dup16 => .dup 16
  | .swap1 => .swap 1
  | .swap2 => .swap 2
  | .swap3 => .swap 3
  | .swap4 => .swap 4
  | .swap5 => .swap 5
  | .swap6 => .swap 6
  | .swap7 => .swap 7
  | .swap8 => .swap 8
  | .swap9 => .swap 9
  | .swap10 => .swap 10
  | .swap11 => .swap 11
  | .swap12 => .swap 12
  | .swap13 => .swap 13
  | .swap14 => .swap 14
  | .swap15 => .swap 15
  | .swap16 => .swap 16

def toStackResult : Except Error (List UInt256) → Except Error OperandStack
  | .error reason => .error reason
  | .ok words => .ok (MemoryStackControl.Stack.fromWords words)

def applyOperation : Operation → OperandStack → Except Error OperandStack
  | .pop, stack => toStackResult (popWords stack.words)
  | .dup depth, stack => toStackResult (duplicateWords depth stack.words)
  | .swap depth, stack => toStackResult (swapWords depth stack.words)

def executePlanned (operation : Operation) (plan : Plan) (state : State) : Outcome :=
  let dispatched := if plan.pcBeforeGas = true then state.advancePc else state
  match debit plan dispatched with
  | .outOfGas afterOutOfGas => .error .outOfGas afterOutOfGas
  | .paid afterGas =>
    if plan.faultsAfterGas = false then
      match applyOperation operation afterGas.stack with
      | .ok result => .success { afterGas with stack := result }
      | .error reason => .error reason afterGas
    else if afterGas.stack.words.length < plan.stackInputs then
      .error .stackUnderflow afterGas
    else if plan.stackGrowth > 0 ∧
        afterGas.stack.words.length + plan.stackGrowth > plan.stackLimit then
      .error .stackOverflow afterGas
    else
      match applyOperation operation afterGas.stack with
      | .ok result => .success { afterGas with stack := result }
      | .error reason => .error reason afterGas

def plan (opcode : Opcode) : Plan :=
  let d := descriptor opcode
  { fixedGas := d.fixedGas
    stackInputs := d.stackInputs
    stackGrowth := d.stackGrowth
    stackLimit := stackLimit
    pcBeforeGas := d.pcOrder == "pc-before-gas"
    gasExhaustsOnFailure := d.faultOrder == "out-of-gas-before-underflow-before-overflow"
    faultsAfterGas := d.gasOrder == "gas-before-stack" }

def execute (opcode : Opcode) (state : State) : Outcome :=
  executePlanned (toOperation opcode) (plan opcode) state

end Eip803x.Generated.StackRearrangementOpcodeKernel
