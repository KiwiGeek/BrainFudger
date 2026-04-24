# MS-DOS `.COM` Format 🤠💾

The `.COM` format is the punk-rock executable format.

No sections. Barely a header. Almost no ceremony. Just "here are bytes, please jump into them."

## What A `.COM` File Actually Is

A DOS `.COM` program is a flat binary image loaded into memory at offset `0x0100` within a single segment.

The loader sets up a Program Segment Prefix (PSP) at offset `0x0000`, then copies the `.COM` file starting at:

```text
CS:0100
```

Execution begins with:

```text
IP = 0100h
```

and typically:

- `CS = DS = ES = SS = PSP segment`
- code and data share that segment

## Memory Layout

```text
segment base + 0000h  PSP
segment base + 0100h  your .COM image begins
```

Everything in the file is addressed relative to origin `0x0100`.

## No Header

Important: a `.COM` file has no formal executable header.

That means:

- the file bytes are the image
- there are no relocations
- there is no import table
- there is no explicit entry-point field

The convention is the format.

## What BrainFudger Puts In The Image

BrainFudger builds one flat image composed of:

1. code bytes
2. data bytes appended after the code

The patcher resolves:

- absolute 16-bit offsets for data references
- relative 16-bit displacements for control flow

using origin `0x0100`.

## Startup Assumptions

The DOS backend begins by making the segment registers sane:

- `push cs`
- `pop ds`
- `push ds`
- `pop es`

That ensures `DS` and `ES` point where the code and data live.

## DOS I/O Services Used Here

### Output One Character

```text
AH = 02h
DL = character
INT 21h
```

BrainFudger uses this for Brainf$#k `.`.

### Read One Byte From Standard Input

```text
AH = 3Fh
BX = 0        ; stdin handle
CX = 1
DS:DX = buffer
INT 21h
```

On return:

- `AX` = bytes read
- `AX = 0` means EOF

BrainFudger uses this for Brainf$#k `,` and zeroes the current cell on EOF.

### Print A `$`-Terminated String

```text
AH = 09h
DS:DX = address of string ending in '$'
INT 21h
```

Used for pointer-bounds error messages.

### Exit To DOS

```text
AH = 4Ch
AL = exit code
INT 21h
```

## Addressing Strategy

Because the entire image sits in one segment:

```text
runtime_offset = 0100h + file_offset
```

for absolute label addresses.

Relative jumps use standard signed displacements from the next instruction.

## Hand-Crafting A `.COM`

1. choose origin `0100h`
2. write 16-bit real-mode machine code
3. append any data after the code
4. patch label references using `origin + target_offset`
5. save the bytes as `something.com`

That is it. No section table. No import directory. No relocation table.

## Example Mental Model

If your file starts with:

```text
B4 02
B2 41
CD 21
B4 4C
B0 00
CD 21
```

that is already a valid `.COM` that prints `A` and exits.

## Constraints

- practical size limit under 64 KB including code, data, and stack
- no relocations
- single-segment worldview
- DOS interrupt conventions instead of imports

It is simple, but not forgiving.
