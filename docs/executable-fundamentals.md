# Executable Fundamentals 🧱⚙️

If you want to hand-craft an executable, you need one core idea:

> an executable file is just a byte layout that convinces a loader to map bytes into memory and begin execution at a known entry point

Everything else is details. Unfortunately, the details are the whole sport.

## The Five Questions Every Executable Answers

Any executable format has to answer these questions somehow:

1. What architecture is this for?
2. Where should the loader put the bytes in memory?
3. Which bytes are code, which are data, and which are metadata?
4. Where should execution begin?
5. What outside services does the program need?

Different formats answer them differently:

- DOS `.COM`: barely answers any of them; relies on conventions
- DOS `MZ .EXE`: answers more via a small header
- PE32 / PE32+: answers them in elaborate Windows-flavored detail

## The Loader's Job

A loader turns a file into a running process. Conceptually it does some version of:

1. read the file header
2. verify magic numbers and machine type
3. allocate memory
4. map or copy sections or segments into memory
5. apply relocations if needed
6. resolve imports if the format supports them
7. initialize registers, stack, and process environment
8. jump to the program entry point

When you hand-build an executable, you are basically pre-negotiating with that loader.

## Three Layers To Keep Separate

### 1. Machine Code

This is the raw instruction stream for the CPU:

- x86 real mode
- x86 protected mode
- x86-64 long mode

### 2. In-Memory Program Layout

This is the runtime shape:

- where the code lives
- where the data lives
- where the tape lives
- where the stack lives
- where imports or jump tables live

### 3. On-Disk Container Format

This is the wrapper the loader understands:

- `.COM`
- `MZ .EXE`
- `PE32`
- `PE32+`

Same logic, different wrapper.

## Absolute Addressing vs Relative Addressing

### Absolute Addressing

You encode the final address or offset directly:

- DOS `.COM`: often absolute offset within the segment
- PE32 x86 in this project: absolute virtual addresses are patched into certain instructions

### Relative Addressing

You encode distance from the next instruction rather than final address:

- `jmp rel16`
- `je rel32`
- RIP-relative addressing on x64 PE

Useful formula:

```text
displacement = target - next_instruction
```

## Labels And Patch Tables

A practical hand-builder workflow:

1. emit bytes
2. when you define a label, record `label -> current offset`
3. when you emit a reference to a not-yet-known label, record:
   - label name
   - patch offset
   - how to compute it
4. once layout is known, resolve the patches

This is exactly how the emitters in BrainFudger work.

## Alignment

Formats often care about alignment because CPUs and loaders do.

Kinds of alignment:

- instruction or data alignment inside a section
- file alignment on disk
- section alignment in memory
- paragraph alignment in DOS headers

Examples from the current emitters:

- PE file alignment: `0x200`
- PE section alignment: `0x1000`
- DOS EXE header size measured in 16-byte paragraphs

## Minimal Runtime Services

A hand-crafted executable almost always needs:

- output
- input
- exit

The mechanism depends on the target:

- DOS real mode: interrupt `21h`
- Win32 PE: imported APIs from `KERNEL32.dll`

## Building A Binary By Hand

General recipe:

1. choose a target format and architecture
2. write a tiny machine-code payload
3. decide where its data lives
4. identify all label references that need patching
5. choose the file layout
6. compute offsets, RVAs, or segment-relative addresses
7. write headers
8. append sections or image bytes
9. patch references
10. test in the actual host or emulator

## Debugging Strategy 🔍

When a hand-built executable fails:

- verify the magic number and machine type
- verify header sizes and entry point
- verify section or file alignment
- verify imported symbols or DOS interrupt conventions
- inspect the first few executed instructions

Most failures come from:

- wrong offset math
- wrong next-instruction base for relative patches
- wrong stack or segment assumptions
- invalid import table layout
- forgotten terminators or padding

Executable formats look mystical until you remember that the loader only sees bytes and arithmetic.
