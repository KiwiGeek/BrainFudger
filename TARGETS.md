# Brainfuck Compiler Target Roadmap

This document tracks current targets and planned expansions, primarily focused on **real executable formats** in the DOS/Windows ecosystem (with room to expand into other real hardware platforms).

---

## ✅ Current Targets

- [x] `win32-x64` (PE32+)
- [x] `win32-x86` (PE32)
- [x] `msdos-com` (.COM, real mode)
- [x] `msdos-exe` (MZ .EXE, real mode)

---

## 🎯 Immediate Next Targets (High Priority)

These fill the most obvious historical and architectural gaps.

### Windows 3.x

- [ ] `win16-ne`
  - New Executable (NE) format
  - Segmented memory model
  - Optional:
    - [ ] minimal GUI app
    - [ ] console-style stub behavior

---

### DOS Runtime Variants

- [ ] `dos-tsr`
  - Terminate-and-stay-resident program
  - Hooks interrupts and remains in memory
  - Distinct execution model from standard COM/EXE

- [ ] `dos-sys`
  - DOS device driver (`.SYS`)
  - Character or block device
  - Entry via driver init strategy routine

---

### Protected Mode DOS

- [ ] `dos-dpmi` (or `dos-extender`)
  - 32-bit protected mode program under DOS
  - DOS/4GW, CWSDPMI, or similar
  - Bridge between DOS and Win32

---

## 🧱 Secondary Targets (Medium Priority)

These expand platform coverage while staying in the same ecosystem.

### Additional Windows Architectures

- [ ] `win32-arm64`
  - PE for Windows on ARM
  - Reuse PE backend, new ISA backend

---

### OS/2

- [ ] `os2-ne`
  - Early OS/2 executable format (similar lineage to Win16)

- [ ] `os2-lx`
  - Later 32-bit OS/2 format
  - More complex loader model

---

## 🧪 Experimental / Cursed Targets (Still Real Systems)

These are valid execution environments but push into more unusual territory.

- [ ] `dos-overlay`
  - Overlay-based executable layout
  - Separate code/data segments loaded on demand

- [ ] `dos-bootsector`
  - 512-byte bootable binary
  - Real-mode execution at `0x7C00`
  - Extreme size constraints
  - Likely requires:
    - peephole optimization
    - loop specialization
    - optional stage-2 loader

---

## 🌍 Non-DOS/Windows Expansion (Future)

Out of scope for now, but planned.

### Firmware / Bare Metal

- [ ] `uefi-x64`
- [ ] `bios-bootloader`

---

### Unix-like Systems

- [ ] `linux-x64-elf`
- [ ] `linux-x86-elf`

---

### Retro Platforms

- [ ] `cpm-com`
- [ ] `apple2-dos33`
- [ ] `c64-prg`

---

## 🧠 Compiler Architecture Goals

To support multiple targets cleanly, the compiler should be structured as:

### Frontend / IR
- Collapse repeated operations
- Detect common loop idioms:
  - `[-]` → clear cell
  - `[->+<]` → move
- Emit compact intermediate representation

### ISA Backends
- x86 (16/32/64)
- ARM64
- (future) 6502, Z80, etc.

### Platform Layers
- DOS (COM/EXE/TSR/SYS)
- Windows (PE32/PE32+/NE)
- OS/2
- (future) others

---

## ⚙️ Useful Feature Flags

These will become necessary as targets expand:

- [ ] `--cell-size` (8/16/32)
- [ ] `--tape-size`
- [ ] `--wrap-cells`
- [ ] `--freestanding`
- [ ] `--entry`
- [ ] `--io-mode` (raw vs host)
- [ ] `--memory-model` (static/stack/fixed)

---

## 🧭 Suggested Implementation Order

1. `win16-ne`
2. `dos-tsr`
3. `dos-sys`
4. `dos-dpmi`
5. `win32-arm64`
6. `os2-ne`
7. `os2-lx`
8. `dos-bootsector` (final boss)

---

## 🏁 End Goal

A brainfuck compiler capable of targeting executable formats across the full historical spectrum of the DOS/Windows ecosystem, including:

- real mode
- protected mode
- segmented Windows
- modern PE
- and deeply cursed runtime models

---

## Notes

- Prefer reusing container formats (MZ → NE → PE) where possible
- Separate ISA concerns from executable format concerns
- Optimize for correctness first, then cursed elegance later