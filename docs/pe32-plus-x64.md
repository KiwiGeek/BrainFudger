# PE32+ for Win32 x64 🚀🪟

This is the 64-bit Windows format used by BrainFudger's `win32-x64` target.

## What Changes From x86 PE32?

Main differences:

- machine type becomes `0x8664`
- optional-header magic becomes `0x20B`
- image base is 64-bit
- stack and heap sizes are 64-bit
- there is no `BaseOfData` field
- import thunks are 64-bit
- the code uses the Windows x64 calling convention
- internal addressing often prefers RIP-relative encodings

## Section Layout

The project still uses three sections:

- `.text`
- `.idata`
- `.data`

Same mental model, different ABI and patching style.

## COFF And Optional Header Essentials

For a minimal x64 PE:

- `Machine = 0x8664`
- `NumberOfSections = 3`
- `SizeOfOptionalHeader = 0xF0`
- `Magic = 0x20B`
- `Subsystem = 3`
- `SectionAlignment = 0x1000`
- `FileAlignment = 0x200`

In this project, the image base is:

```text
0x0000000140000000
```

## Why RIP-Relative Addressing Matters

On x64 Windows, code commonly uses RIP-relative forms:

```text
lea rbx, [rip + tape]
call qword ptr [rip + WriteFile_iat]
cmp dword ptr [rip + io_result], 0
```

Patch math becomes:

```text
displacement = target_rva - next_instruction_rva
```

That same formula works for branches and RIP-relative data references.

## Import Table

Same four imported functions:

- `GetStdHandle`
- `ReadFile`
- `WriteFile`
- `ExitProcess`

Difference:

- import lookup table entries are 64 bits
- IAT entries are 64 bits

The import descriptor still stores 32-bit RVAs.

## Windows x64 Calling Convention

First four integer or pointer arguments:

- `RCX`
- `RDX`
- `R8`
- `R9`

Rules that matter:

- caller allocates 32 bytes of shadow space
- stack must be 16-byte aligned at call boundaries

BrainFudger reserves stack space in the prologue and populates registers before each API call.

## Register Allocation Used Here

- `RBX` = current tape pointer
- `R12` = tape start
- `R13` = tape end
- `R14` = stdin handle
- `R15` = stdout handle
- `RDI` = stderr handle

## Runtime Data In `.data`

The x64 data section contains:

- tape
- `tape_end`
- `io_result` as 8 bytes
- pointer error strings

The standard handles stay in preserved registers rather than being spilled into `.data`.

## Entry Sequence

The startup flow is:

1. reserve stack space
2. call `GetStdHandle` for stdin, stdout, and stderr
3. store those handles in preserved registers
4. load tape start and tape end with RIP-relative `lea`
5. execute translated Brainfuck
6. call `ExitProcess(0)`

## Patch Math

The code patcher resolves labels against:

- `.text`
- `.idata`
- `.data`

Then it writes a 32-bit displacement relative to the next instruction. That works for:

- conditional and unconditional branches
- RIP-relative data references
- RIP-relative indirect calls through the IAT

## Hand-Crafting A Minimal PE32+ File

1. write DOS header and `e_lfanew`
2. write `PE\0\0`
3. write COFF header with `Machine = 0x8664`
4. write PE32+ optional header with `Magic = 0x20B`
5. fill import and IAT data-directory entries
6. write section headers
7. build `.idata`
8. build `.data`
9. build `.text` using placeholders for RIP-relative displacements
10. assign raw pointers and RVAs
11. patch `.idata` internal label references
12. patch `.text` references against final RVAs
13. pad raw data to alignment

## Common Failure Modes 😵

- using PE32 optional-header magic for an x64 file
- writing 32-bit import entries instead of 64-bit ones
- forgetting shadow space before API calls
- breaking stack alignment
- patching relative references from the wrong next-instruction offset
- forgetting that data directories use RVAs, not file offsets

If you can hand-build one of these, a lot of Windows executable magic stops feeling magical and starts feeling like arithmetic with paperwork.
