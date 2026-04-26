# Intermediate Opcodes

The compiler does not hand raw Brainf$#k text to the emitters anymore.

It first lowers the source into a shared intermediate instruction stream. That stream is compact enough to carry the simple frontend optimizations, but still close enough to the original language that every emitter can translate it directly.

## Why This Layer Exists

Without an intermediate form, every backend has to redo the same work:

- strip comments
- validate loop matching
- compress repeated `+`, `-`, `>`, and `<`
- decide how optional extension tokens are represented

That is wasted duplication. The intermediate layer moves those concerns into the frontend once.

## Opcode Set

| Opcode | Operand | Meaning |
| --- | --- | --- |
| `MovePointer` | signed integer | move the tape pointer right for positive values, left for negative values |
| `AddToCell` | signed integer | add to the current cell for positive values, subtract for negative values |
| `WriteByte` | none | write the current cell |
| `ReadByte` | none | read one byte into the current cell |
| `LoopStart` | matching instruction index | start of a loop |
| `LoopEnd` | matching instruction index | end of a loop |
| `RandomByte` | none | extension: replace current cell with a pseudorandom byte |
| `ClearTerminal` | none | extension: emit a best-effort terminal clear sequence |
| `Delay` | none | extension: perform a best-effort delay using the current cell as the scale input |

## Lowering Rules

The frontend walks the source left to right and applies these rules:

1. ignore all non-significant characters
2. reject `?`, `!`, or `~` if their feature flags are disabled
3. collapse runs of `>` and `<` into one `MovePointer`
4. collapse runs of `+` and `-` into one `AddToCell`
5. emit loop instructions with matching instruction indexes resolved during lowering

Examples:

```text
>>>>>
```

becomes:

```text
MovePointer +5
```

```text
------
```

becomes:

```text
AddToCell -6
```

```text
[->+<]
```

becomes:

```text
LoopStart -> 5
AddToCell -1
MovePointer +1
AddToCell +1
MovePointer -1
LoopEnd -> 0
```

## Loop Representation

Loops are stored as explicit `LoopStart` and `LoopEnd` instructions, each carrying the matching instruction index.

That gives the backends enough information to emit labels and jumps without having to rebuild a bracket stack from source text.

## Relationship To The Backends

Every emitter now accepts the same lowered program and is responsible only for:

- tape layout
- register allocation
- I/O routines
- runtime helper behavior for extensions
- container format details such as PE, ELF, Mach-O, or DOS headers

The emitters no longer need their own source sanitizers or token compressors.
