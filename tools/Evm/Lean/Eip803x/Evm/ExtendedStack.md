# EIP-8024 extended-stack reference

`ExtendedStack.lean` implements the pinned EIP-8024 immediate decoders and
top-first stack transformations for `DUPN`, `SWAPN`, and `EXCHANGE`. It rejects
the specified forbidden immediate ranges, distinguishes invalid immediates
from stack underflow/overflow, and inherits the 1,024-word bounded stack.

This is a handwritten pure-stack reference only. Opcode gas, PC movement,
truncated immediate reads, dispatch reachability, and production
extraction/refinement remain separate obligations. The module does prove the
key jump-destination composition fact: every valid EIP-8024 immediate is
neither `JUMPDEST` nor `PUSH1`..`PUSH32`. Therefore a legacy scanner that
examines the valid immediate as a width-one opcode computes the same
destination boundaries as the EIP scanner that skips it; forbidden immediates
remain at an instruction boundary in both interpretations.
