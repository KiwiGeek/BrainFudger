# Linux ELF Executables 🐧⚙️

This document explains the syscall-only ELF targets used by BrainFudger:

- `linux-x64`
- `linux-x86`
- `linux-arm64`

## Big Picture

For these targets, the file is a minimal main executable in ELF format with one loadable segment:

1. ELF header
2. one `PT_LOAD` program header
3. code bytes
4. data bytes

There is no interpreter path, no section-table dependency at runtime, and no import mechanism like PE.

The loader only needs enough metadata to map the image, jump to the entry point, and let the code talk to the kernel directly with syscalls.

## What The Three Linux Targets Share

All three Linux emitters use the same broad runtime model:

- one flat tape in writable data
- one code block with translated Brainf$#k instructions
- pointer-underflow and pointer-overflow checks
- direct reads from stdin
- direct writes to stdout and stderr
- direct process exit syscall

That means the format work splits cleanly into two layers:

1. architecture-specific machine code
2. architecture-appropriate ELF container

## ELF Structure Used Here

BrainFudger keeps the file intentionally small:

- executable file type: `ET_EXEC`
- one program header: `PT_LOAD`
- read, write, and execute permissions on the mapped image
- no section headers required for execution

That last point matters. Tools like `file` will happily identify these binaries even though the section table is omitted. The Linux kernel does not need section headers to run a normal ELF executable.

## Segment Layout

The layout is simple:

- ELF header
- program header table
- alignment padding
- code
- alignment padding
- data

The writer then points the entry address at the beginning of the code region.

The data region contains:

- `tape`
- `tape_end`
- pointer-before-start message
- pointer-past-end message

## Why Program Headers Matter More Than Section Headers

For a runnable ELF, program headers are the real contract with the loader.

The loader cares about:

- file offset
- virtual address
- file size
- memory size
- permissions
- alignment

Section headers are mainly for linkers, debuggers, and inspection tools. They are not needed for the kernel to launch a small hand-built executable.

## `linux-x64`

This target emits a 64-bit ELF for x86-64 Linux.

Important file identity values:

- ELF class: 64-bit
- machine: `EM_X86_64` (`0x3E`)
- entry point: start of generated x64 code

The runtime uses:

- `RBX` = current tape pointer
- `R12` = tape start
- `R13` = tape end

Syscalls follow the normal x86-64 Linux ABI:

- `RAX` = syscall number
- `RDI`, `RSI`, `RDX`, `R10`, `R8`, `R9` = arguments
- `syscall`

Used syscalls:

- `read` = `0`
- `write` = `1`
- `exit` = `60`

## `linux-x86`

This target emits a 32-bit ELF for x86 Linux.

Important file identity values:

- ELF class: 32-bit
- machine: `EM_386` (`0x03`)
- entry point: start of generated x86 code

The runtime uses:

- `EBX` = current tape pointer
- `ESI` = tape start
- `EDI` = tape end

Syscalls use the classic Linux x86 interrupt ABI:

- `EAX` = syscall number
- `EBX`, `ECX`, `EDX`, `ESI`, `EDI`, `EBP` = arguments
- `int 0x80`

Used syscalls:

- `read` = `3`
- `write` = `4`
- `exit` = `1`

On a 64-bit Linux machine, this target can still run if the kernel has 32-bit compatibility enabled. That is why `linux-x86 --run` works on many x64 Linux hosts.

## `linux-arm64`

This target emits a 64-bit ELF for AArch64 Linux.

Important file identity values:

- ELF class: 64-bit
- machine: `EM_AARCH64` (`0xB7`)
- entry point: start of generated ARM64 code

The runtime uses:

- `x19` = current tape pointer
- `x20` = tape start
- `x21` = tape end

Syscalls follow the standard Linux ARM64 ABI:

- `x8` = syscall number
- `x0` through `x5` = arguments
- `svc 0`

Used syscalls:

- `read` = `63`
- `write` = `64`
- `exit` = `93`

Addressing for data labels uses `ADRP` plus `ADD`, just like the Mach-O ARM64 target, because page-relative address construction is the practical way to reach mapped data reliably.

## Runtime Behavior

Across all Linux targets:

- `.` writes one byte from the current tape cell to stdout
- `,` reads one byte from stdin into the current tape cell
- EOF or a failed read stores `0`
- `>` and `<` check bounds before accepting the new pointer
- pointer underflow and overflow print an error to stderr and exit with code `1`

That means the generated binaries are self-contained in behavior as well as format. No startup runtime is hiding behind the curtain.

## Hand-Crafting A Minimal Linux ELF

At a high level:

1. choose the architecture and matching ELF class
2. write the ELF header
3. write one `PT_LOAD` program header
4. decide the mapped base address and entry point
5. place code after the headers with alignment padding
6. place writable data after the code
7. patch code references to labels in code and data
8. mark the final file executable

The hard part is not the ELF shell. It is making the machine code and patch math line up with the chosen ISA.

## Common Failure Modes 😵

- wrong `e_machine` for the instruction set
- wrong ELF class
- entry point aimed at padding instead of code
- program header sizes or addresses that do not match the real file layout
- relative branch patching from the wrong next-instruction offset
- absolute data references written with the wrong width
- forgetting that x86 and x64 Linux use different syscall ABIs entirely
- assuming ARM64 Linux syscalls use the same register as Darwin ARM64

That last one is a reliable way to produce a binary that looks fine in `file` and does absolutely nothing useful when run.

## Why This Matters Beyond Brainf$#k

These ELF targets are good examples of the smallest useful Linux-native executable model:

- valid ELF container
- direct entry point
- direct syscalls
- no dependency on a language runtime

Swap the Brainf$#k payload out for any other hand-written machine code and the container rules stay the same. The Linux side of the problem is mostly loader arithmetic plus the syscall ABI for the chosen CPU.
