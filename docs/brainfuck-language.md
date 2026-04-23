# Brainfuck Language Guide 🧠➡️⬅️➕➖

Brainfuck is tiny, hostile, and surprisingly teachable once you stop expecting it to be polite.

This document covers the language itself, independent of any binary format.

## Core Model

A Brainfuck program operates on:

- a tape of cells
- a data pointer that points at the current cell
- a program counter that steps through instructions
- an input stream
- an output stream

In the classic model:

- each cell is 8 bits
- arithmetic wraps modulo 256
- the tape is conceptually unbounded

In BrainFudger's generated binaries:

- cells are 8-bit bytes
- arithmetic wraps naturally because the emitted instructions modify bytes
- the tape is fixed-size, not infinite
- moving before the beginning of the tape or past the end is a runtime error

## The Eight Instructions

Everything else in a Brainfuck source file is comment noise.

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

## Operational Semantics

You can think of Brainfuck as this little machine:

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

This is where Brainfuck implementations become little goblins.

The original language did not standardize EOF behavior. Common choices are:

- leave the cell unchanged
- store `0`
- store `255`
- signal an error

BrainFudger's emitted binaries do this:

- attempt to read one byte
- if the read fails or reaches EOF, store `0` in the current cell

That makes programs deterministic across the currently supported targets.

## Comments And Ignored Characters

Brainfuck only cares about these eight tokens:

```text
><+-.,[]
```

Everything else is ignored. BrainFudger sanitizes the source by stripping everything except the eight significant tokens before validation or code generation.

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

```brainfuck
[-]
```

### Move A Value Right

```brainfuck
[->+<]
```

### Copy A Value

Typical copy requires a scratch cell:

```brainfuck
[->+>+<<]>>[-<<+>>]
```

### Emit ASCII Text

Brainfuck string literals are usually done by building ASCII values in cells and printing them:

```brainfuck
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

```brainfuck
+++++++++
```

into a single "add 10" operation. BrainFudger does this for:

- `+`
- `-`
- `>`
- `<`

### Tape Bounds

Pure Brainfuck pretends the tape goes on forever. BrainFudger allocates a fixed tape and inserts bounds checks:

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

## Hand-Compiling Brainfuck To Machine Code

General recipe:

1. choose where the tape lives
2. dedicate one register to the current cell pointer
3. dedicate one or two registers to tape bounds if you need safety checks
4. translate each Brainfuck token into machine instructions
5. patch loop jumps once labels are known
6. add OS-specific output, input, and exit routines
7. wrap the resulting code in an executable container

That last step is where the format-specific docs take over.

## Sharp Edges 😈

- EOF behavior is implementation-defined
- cell width is implementation-defined
- tape length is implementation-defined
- pointer underflow and overflow behavior is implementation-defined

If you are writing Brainfuck that must run on many implementations, avoid relying on anything except the eight core instruction meanings.
