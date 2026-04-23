# MS-DOS MZ `.EXE` Format 🧾💾

The DOS `MZ` executable format is the grown-up sibling of `.COM`.

It still runs in 16-bit real mode, but it adds a real header so DOS knows more about the program before loading it.

## Why `MZ`?

The magic number is:

```text
4D 5A
```

which is `MZ`, traditionally associated with Mark Zbikowski.

## What The Header Buys You

Compared to `.COM`, an `MZ .EXE` gains:

- an explicit header
- relocation support in the general format
- explicit initial `CS:IP` and `SS:SP`
- minimum and maximum memory request fields
- image data separated from the header

BrainFudger uses a very small subset of the full format because the current emitter targets simple single-segment programs.

## Layout Overview

```text
[MZ header]
[header padding]
[load image]
```

## Header Fields Used By BrainFudger

The emitter writes a compact 32-byte header, which is `2` paragraphs of `16` bytes each.

Important fields:

- signature: `MZ`
- bytes in last block
- blocks in file
- relocation count = `0`
- header size in paragraphs = `2`
- min extra paragraphs = `0`
- max extra paragraphs = `0xFFFF`
- initial `SS = 0`
- initial `SP = total image size`
- checksum = `0`
- initial `IP = 0`
- initial `CS = 0`
- relocation table offset = `0x001C`
- overlay number = `0`

## What This Means In Practice

The emitter builds:

1. the code and data image
2. extra zeroed bytes for stack space
3. the `MZ` header
4. the final file by concatenating header and image-with-stack

The image is intentionally limited to a single-segment world:

- if total image exceeds roughly 64 KB, the emitter rejects it

## Stack Setup

The current emitter chooses:

- `SS = 0`
- `SP = total image size`

Because DOS loads the image relative to the program segment, `SS:SP` lands at the end of the single loaded image, where the appended stack space lives.

## No Relocations In The Current Writer

General `MZ` files support relocations via a relocation table.

BrainFudger's current `msdos-exe` writer does not emit relocations and instead keeps the image simple enough that:

- code and data live in one segment
- internal absolute offsets are segment-relative offsets inside that segment

## Shared Payload With `.COM`

The DOS `.EXE` target reuses the same Brainfuck code and data generation logic as the `.COM` target:

- same Brainfuck lowering
- same DOS interrupt services
- same data strings
- same patching model for label references

The difference is the outer container and startup conventions.

## Hand-Crafting A Minimal `MZ` EXE

1. write your 16-bit code and data image
2. append stack space if needed
3. compute final file size
4. compute:
   - pages in file
   - bytes in last page
   - header size in paragraphs
5. write the `MZ` header
6. pad header to `header_paragraphs * 16`
7. append the image

## Field Math

Let:

- `headerBytes = headerParagraphs * 16`
- `fileSize = headerBytes + imageSize`

Then:

- `blocksInFile = ceil(fileSize / 512)`
- `bytesInLastBlock = fileSize mod 512`, except `512` if evenly divisible

That is exactly the math the writer uses.

## Why Use `MZ` Instead Of `.COM`?

Use `MZ` when you want:

- explicit stack setup
- a real executable header
- room to grow toward relocations or multi-segment ideas
- something closer to a historically normal DOS executable

Use `.COM` when you want brutal simplicity.

## Common Failure Modes

- wrong page-count math
- wrong header paragraph count
- forgetting to pad the header
- `SP` pointing outside the loaded image
- emitting an image larger than the chosen memory model can tolerate

Learning `MZ` teaches the important lesson that even old executable formats are contracts with the loader, not just random bytes in a trench coat.
