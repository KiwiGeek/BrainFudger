# Mach-O for Apple Silicon 🍎⚙️

This document explains the 64-bit Mach-O executable format used by BrainFudger's `osx-arm64` target.

## Big Picture

For this target, the file is a thin ARM64 Mach-O main executable. The loader expects:

1. a Mach header
2. a sequence of load commands
3. page-aligned segment contents
4. a valid entry point
5. enough linker metadata that modern macOS tools do not immediately reject the file as cursed nonsense

The important difference from PE and MZ is that Mach-O describes the process image mostly through load commands rather than one giant fixed header block.

## CPU And File Identity

Key header values for this target:

- magic: `0xFEEDFACF` for 64-bit Mach-O
- CPU type: `0x0100000C` for ARM64
- CPU subtype: `0x00000000`
- file type: `MH_EXECUTE`
- flags: include `MH_NOUNDEFS`, `MH_DYLDLINK`, `MH_TWOLEVEL`, and `MH_PIE`

Those flags tell the system "this is a normal dynamically linked position-independent executable," which is much closer to what current macOS wants than a freestanding science project.

## Load Commands Used Here

BrainFudger's Mach-O writer currently emits:

- `LC_SEGMENT_64` for `__PAGEZERO`
- `LC_SEGMENT_64` for `__TEXT`
- `LC_SEGMENT_64` for `__DATA`
- `LC_SEGMENT_64` for `__LINKEDIT`
- `LC_LOAD_DYLINKER`
- `LC_LOAD_DYLIB` for `/usr/lib/libSystem.B.dylib`
- `LC_MAIN`
- `LC_BUILD_VERSION`
- `LC_SYMTAB`
- `LC_DYSYMTAB`

That list matters because modern macOS validation is picky. A file that is "close enough" structurally can still fail strict validation or die at launch.

## Segment Layout

The image layout is intentionally conventional:

- `__PAGEZERO`
  - unmapped guard region at virtual address `0`
  - catches null-pointer dereferences the old-fashioned way
- `__TEXT`
  - readable and executable
  - contains the Mach header, load commands, and the `__text` section
- `__DATA`
  - readable and writable
  - contains the tape and message data in the `__data` section
- `__LINKEDIT`
  - read-only metadata region for symbol-table related data

Typical page size assumptions in this emitter are `0x4000`, which matches the 16 KB page world common on Apple Silicon macOS.

## Sections Used Here

Inside those segments, BrainFudger keeps it simple:

- `__TEXT,__text`
  - translated ARM64 machine code
- `__DATA,__data`
  - tape storage
  - tape end marker
  - pointer error strings

Unlike the PE emitters, there is no import-address table section with patchable thunks. The current implementation talks to the kernel using syscalls from ARM64 code.

## Entry Point

`LC_MAIN` carries the entry offset. It is not a virtual address. It is a file-relative offset into the mapped image that dyld uses as the program entry.

For a hand-built executable:

1. decide where the `__text` bytes begin in the file
2. point `LC_MAIN.entryoff` at that offset
3. make sure the first instructions at that location are valid ARM64

If `entryoff` is wrong, the program does not "sort of work." It goes straight into the rocks.

## ARM64 Runtime Shape

BrainFudger's ARM64 code uses:

- `x19` = current tape pointer
- `x20` = tape start
- `x21` = tape end

The prologue loads those addresses with page-relative addressing:

```text
adrp/add -> tape
adrp/add -> tape_end
```

That approach matters because modern Mach-O binaries are position-independent. Hard-coded absolute addresses are not your friends here.

## Syscalls In This Target

The current implementation uses Darwin syscalls directly:

- `read`
- `write`
- `exit`

The calling pattern is:

- syscall number in `x16`
- arguments in `x0`, `x1`, `x2`, ...
- `svc 0x80`

For example:

```text
x0 = file descriptor
x1 = buffer pointer
x2 = byte count
x16 = syscall number
svc 0x80
```

Standard descriptors are the usual Unix ones:

- `0` = stdin
- `1` = stdout
- `2` = stderr

## Data Layout In `__DATA`

The data section contains:

- the Brainfuck tape
- `tape_end`
- pointer-before-start message
- pointer-past-end message

There is no import bookkeeping here because this target does not currently call through `libSystem` symbols. The dylib load command is present to make the executable look like a normal dynamically linked main executable to the platform.

## Header Math You Actually Need

When hand-crafting Mach-O, the annoying numbers are:

- `ncmds`: total number of load commands
- `sizeofcmds`: total byte size of all load commands
- segment `fileoff` / `filesize`
- segment `vmaddr` / `vmsize`
- section `offset` / `addr`

These must agree with one another. A mismatch can produce:

- `codesign` strict-validation failures
- `otool` warnings like `Inconsistent sizeofcmds`
- launch-time death with a very unhelpful `killed`

Mach-O has a special talent for failing in ways that feel personal.

## Minimal Hand-Build Checklist ✅

1. write the 64-bit Mach header
2. choose your full load-command set up front
3. compute `ncmds` and `sizeofcmds` correctly
4. lay out `__PAGEZERO`, `__TEXT`, `__DATA`, and `__LINKEDIT`
5. define `__text` and `__data` sections with matching file and VM addresses
6. emit ARM64 code and data at page-aligned offsets
7. point `LC_MAIN` at the true code entry offset
8. populate symbol-table and dynamic-symbol-table commands consistently
9. make the final file executable
10. ad-hoc sign it on macOS if you want to run it locally

## Common Failure Modes 😵‍💫

- wrong `ncmds`
- wrong `sizeofcmds`
- section offsets that do not line up with segment file ranges
- `entryoff` pointing into headers or padding
- forgetting `__LINKEDIT`
- omitting enough dynamic-linker metadata to satisfy modern validation
- assuming "unsigned" and "structurally invalid" are the same problem
- using absolute addressing instead of PC-relative ARM64 addressing

## Practical Test Flow On A Mac

After copying the file over:

```bash
chmod +x ./hello
codesign -s - -f ./hello
./hello
```

Useful inspection commands:

```bash
codesign -dvvv ./hello
otool -l ./hello
```

If `otool` complains about inconsistent command sizes or segment math, fix the file first. Code signing cannot save a binary whose structure is already wrong.

## Why This Matters Beyond Brainfuck

None of this is Brainfuck-specific. Swap the payload out for any other ARM64 code and the container rules stay the same:

- identify the file correctly
- describe the mapped image coherently
- give dyld a sane entry point
- make the validation and signing toolchain stop screaming

That is the whole Mach-O game, with only a small amount of ceremonial suffering.
