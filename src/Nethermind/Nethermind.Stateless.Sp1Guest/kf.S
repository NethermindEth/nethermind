.section .text.syscall_keccak_f,"ax",@progbits
.global syscall_keccak_f
.type   syscall_keccak_f, @function
syscall_keccak_f:
    tail    zkvm_keccak_permute
    .size   syscall_keccak_f, .-syscall_keccak_f
