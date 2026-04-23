# BrainFudger Docs 📚✨

Welcome to the cursed library.

This folder has two jobs:

1. explain Brainfuck itself clearly enough that somebody can read or write it without vibes-only guessing
2. explain the executable formats BrainFudger emits well enough that you could hand-craft a working binary with a hex editor, patience, and the correct amount of spite

## Reading Order

- [Brainfuck Language](./brainfuck-language.md): syntax, semantics, I/O, common idioms, and practical gotchas
- [Executable Fundamentals](./executable-fundamentals.md): the general mental model for turning bytes into runnable programs
- [PE32 for Win32 x86](./pe32-x86.md): 32-bit Windows executables
- [PE32+ for Win32 x64](./pe32-plus-x64.md): 64-bit Windows executables
- [MS-DOS `.COM`](./msdos-com.md): the tiny real-mode flat image format
- [MS-DOS MZ `.EXE`](./msdos-mz-exe.md): segmented DOS executables with an `MZ` header

## What These Docs Are Trying To Teach

If you want to craft an executable by hand, you need to think in layers:

- source language semantics
- machine instructions
- in-memory layout
- on-disk container format
- loader expectations
- OS services for I/O, process exit, and imports

That stack is the whole game. BrainFudger happens to automate it for Brainfuck, but the docs are written to be useful even if your payload is not Brainfuck at all.

## Format Coverage

The project currently emits four real binary targets:

| Target | Container | CPU mode | Typical host |
| --- | --- | --- | --- |
| `win32-x64` | PE32+ | x86-64 long mode | modern 64-bit Windows |
| `win32-x86` | PE32 | 32-bit x86 protected mode | 32-bit Windows, WOW64, some ARM64 Windows |
| `msdos-com` | raw `.COM` image | 16-bit x86 real mode | MS-DOS, FreeDOS, DOSBox |
| `msdos-exe` | MZ `.EXE` | 16-bit x86 real mode | MS-DOS, FreeDOS, DOSBox |

## Important Disclaimer ⚠️

These docs aim to be practical, not encyclopedic in the "reprint every field from every historical spec" sense. They focus hardest on:

- the fields that matter for a minimal runnable file
- the loader behavior you actually rely on
- the parts BrainFudger itself implements today

So if a format has 40 weird corners and BrainFudger uses 6 of them, those 6 get the royal treatment.
