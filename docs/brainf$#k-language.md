# Brainf$#k Language Guide 🧠➡️⬅️➕➖

Brainf$#k is tiny, hostile, and surprisingly teachable once you stop expecting it to be polite.

This document covers the language itself, independent of any binary format.

## Core Model

A Brainf$#k program operates on:

- a tape of cells
- a data pointer that points at the current cell
- a program counter that steps through instructions
- an input stream
- an output stream

In the classic model:

- each cell is 8 bits
- arithmetic wraps modulo 256
- the tape is conceptually unbounded

In the generated binaries described here:

- cells are 8-bit bytes
- arithmetic wraps naturally because the emitted instructions modify bytes
- the tape is fixed-size, not infinite
- moving before the beginning of the tape or past the end is a runtime error

## The Eight Instructions

Everything else in a Brainf$#k source file is comment noise.

| Token | Meaning |
| --- | --- |
| `>` | move the data pointer right by one cell |
| `<` | move the data pointer left by one cell |
| `+` | increment the current cell |
| `-` | decrement the current cell |
| `.` | write the current cell as one output byte |
| `,` | read one input byte into the current cell |
| `[` | if current cell is zero, jump forward past the matching `]` |
| `]` | if current cell is non-zero, jump back to the matching `[` |

## Optional Extension Instructions

The compiler also supports three opt-in extension commands. They are disabled by default and must be enabled explicitly through CLI flags or GUI checkboxes.

| Token | Meaning |
| --- | --- |
| `?` | replace the current cell with a pseudorandom byte |
| `!` | emit a best-effort terminal clear sequence |
| `~` | perform a best-effort delay based on the current cell value |

These are deliberately outside the core language. If the corresponding feature is not enabled, using the token is a compile-time error.

## Operational Semantics

You can think of Brainf$#k as this little machine:

```text
state = {
  tape: byte[],
  ptr: integer,
  pc: integer
}
```

Instruction behavior:

- `>`: `ptr = ptr + 1`
- `<`: `ptr = ptr - 1`
- `+`: `tape[ptr] = (tape[ptr] + 1) mod 256`
- `-`: `tape[ptr] = (tape[ptr] - 1) mod 256`
- `.`: emit `tape[ptr]`
- `,`: `tape[ptr] = readByte()`, with implementation-defined EOF behavior
- `[`: if `tape[ptr] == 0`, set `pc` to instruction after matching `]`
- `]`: if `tape[ptr] != 0`, set `pc` to instruction after matching `[`

## Input Semantics

This is where Brainf$#k implementations become little goblins.

The original language did not standardize EOF behavior. Common choices are:

- leave the cell unchanged
- store `0`
- store `255`
- signal an error

The emitted binaries described here do this:

- attempt to read one byte
- if the read fails or reaches EOF, store `0` in the current cell

That makes programs deterministic across the currently supported targets.

## Comments And Ignored Characters

Core Brainf$#k only cares about these eight tokens:

```text
><+-.,[]
```

Everything else is ignored. When the optional extension flags are enabled, the compiler also treats `?`, `!`, and `~` as significant tokens before lowering to the intermediate opcode stream.

## Loop Matching

`[` and `]` must be balanced.

Practical compiler strategy:

1. scan left to right
2. push index of each `[`
3. when you hit `]`, pop one `[`
4. error if you ever try to pop from an empty stack
5. error at the end if anything is still on the stack

That is exactly the strategy used in the compiler frontend.

## Common Idioms

### Clear A Cell

```text
[-]
```

### Move A Value Right

```text
[->+<]
```

### Copy A Value

Typical copy requires a scratch cell:

```text
[->+>+<<]>>[-<<+>>]
```

### Emit ASCII Text

Brainf$#k string literals are usually done by building ASCII values in cells and printing them:

```text
+++++++++[>+++++++>++++++++++>+++>+<<<<-]
>++.
>+.
++++++.
.
++.
```

## Practical Compiler Patterns

### Compression Of Repeated Operations

Compilers often compress runs like:

```text
+++++++++
```

into a single "add 10" operation. The compiler described here does this for:

- `+`
- `-`
- `>`
- `<`

### Shared Lowering Step

The compiler frontend does not hand raw source text directly to each backend anymore.

Instead it:

1. filters the source down to significant tokens
2. validates bracket matching
3. lowers repeated arithmetic and pointer runs into a compact opcode stream
4. sends that shared intermediate program to the selected emitter

That keeps parsing, validation, and simple peephole compression out of the platform backends.

### Tape Bounds

Pure Brainf$#k pretends the tape goes on forever. The implementation described here allocates a fixed tape and inserts bounds checks:

- moving left of the first cell prints an error and exits
- moving right at or past the end prints an error and exits

## Minimal Interpreter Pseudocode

```text
sanitize source to only ><+-.,[]
validate bracket matching
pc = 0
ptr = 0
tape = zeroed byte array

while pc < source.length:
  switch source[pc]:
    '>': ptr++
    '<': ptr--
    '+': tape[ptr]++
    '-': tape[ptr]--
    '.': writeByte(tape[ptr])
    ',': tape[ptr] = readByteOrZeroOnEof()
    '[': if tape[ptr] == 0: pc = matchingRightBracket[pc]
    ']': if tape[ptr] != 0: pc = matchingLeftBracket[pc]
  pc++
```

## Hand-Compiling Brainf$#k To Machine Code

General recipe:

1. choose where the tape lives
2. dedicate one register to the current cell pointer
3. dedicate one or two registers to tape bounds if you need safety checks
4. translate each Brainf$#k token into machine instructions
5. patch loop jumps once labels are known
6. add OS-specific output, input, and exit routines
7. wrap the resulting code in an executable container

That last step is where the format-specific docs take over.

## Sharp Edges 😈

- EOF behavior is implementation-defined
- cell width is implementation-defined
- tape length is implementation-defined
- pointer underflow and overflow behavior is implementation-defined

If you are writing Brainf$#k that must run on many implementations, avoid relying on anything except the eight core instruction meanings.
