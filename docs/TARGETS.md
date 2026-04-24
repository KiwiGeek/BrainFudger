# BrainFudger Target Roadmap

This document tracks current targets and planned expansions, primarily focused on **real executable formats** in the DOS/Windows ecosystem, with room to branch into other real hardware platforms later.

---

## Current Targets

- [x] `win-x64` (PE32+)
- [x] `win-x86` (PE32)
- [x] `msdos-com` (`.COM`, real mode)
- [x] `msdos-exe` (`MZ .EXE`, real mode)
- [x] `osx-arm64` (Mach-O, Apple Silicon)

---

## Immediate Next Targets

### Windows 3.x

- [ ] `win16-ne`
  - New Executable (NE) format
  - Segmented memory model
  - Optional:
    - [ ] minimal GUI app
    - [ ] console-style stub behavior

### DOS Runtime Variants

- [ ] `dos-tsr`
  - Terminate-and-stay-resident program
  - Hooks interrupts and remains in memory
  - Distinct execution model from standard COM/EXE

- [ ] `dos-sys`
  - DOS device driver (`.SYS`)
  - Character or block device
  - Entry via driver init strategy routine

### Protected Mode DOS

- [ ] `dos-dpmi` (or `dos-extender`)
  - 32-bit protected mode program under DOS
  - DOS/4GW, CWSDPMI, or similar
  - Bridge between DOS and Win32

---

## Secondary Targets

### Additional Windows Architectures

- [ ] `win32-arm64`
  - PE for Windows on ARM
  - Reuse PE backend, new ISA backend

### macOS

- [ ] `osx-x64`
  - Mach-O for Intel Macs

- [ ] `osx-universal`
  - fat Mach-O wrapper containing multiple architecture slices
  - likely composed from `osx-arm64` + `osx-x64`

### OS/2

- [ ] `os2-ne`
  - Early OS/2 executable format

- [ ] `os2-lx`
  - Later 32-bit OS/2 format
  - More complex loader model

---

## Experimental And Delightfully Cursed Targets

- [ ] `dos-overlay`
  - Overlay-based executable layout
  - Separate code/data segments loaded on demand

- [ ] `dos-bootsector`
  - 512-byte bootable binary
  - Real-mode execution at `0x7C00`
  - Extreme size constraints

---

## Non-DOS/Windows Expansion

### Firmware / Bare Metal

- [ ] `uefi-x64`
- [ ] `bios-bootloader`

### Unix-like Systems

- [ ] `linux-x64-elf`
- [ ] `linux-x86-elf`

### Retro Platforms

- [ ] `cpm-com`
- [ ] `apple2-dos33`
- [ ] `c64-prg`

---

## Compiler Architecture Goals

### Frontend / IR

- collapse repeated operations
- detect common loop idioms:
  - `[-]` -> clear cell
  - `[->+<]` -> move
- emit a compact intermediate representation

### ISA Backends

- x86 (16/32/64)
- ARM64
- future weirdness: 6502, Z80, and friends

### Platform Layers

- DOS (COM/EXE/TSR/SYS)
- Windows (PE32/PE32+/NE)
- OS/2
- future others

---

## Useful Feature Flags

- [ ] `--cell-size` (8/16/32)
- [ ] `--tape-size`
- [ ] `--wrap-cells`
- [ ] `--freestanding`
- [ ] `--entry`
- [ ] `--io-mode` (raw vs host)
- [ ] `--memory-model` (static/stack/fixed)

---

## Suggested Implementation Order

1. `win16-ne`
2. `dos-tsr`
3. `dos-sys`
4. `dos-dpmi`
5. `win32-arm64`
6. `os2-ne`
7. `os2-lx`
8. `dos-bootsector` (final boss)

---

## End Goal

A Brainf$#k compiler capable of targeting executable formats across the historical spectrum of the DOS/Windows ecosystem, including:

- real mode
- protected mode
- segmented Windows
- modern PE
- and a few runtime models that should probably not exist but clearly want to
