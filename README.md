# BrainFucker

`BrainFucker` is a small C# Brainfuck compiler with a pluggable binary emitter interface.  It currently ships with Win32 x64, Win32 x86, and MS-DOS backends.

## What it does

- Reads a Brainfuck source file
- Validates matching loop brackets
- Generates a valid native executable
- Generates tape bounds checks so invalid pointer moves fail with an error

## Usage

```powershell
BrainFucker.exe .\bf_source\hello.bf -o hello.exe
```

```powershell
BrainFucker.exe .\bf_source\hello.bf -o hello.exe --target win32-x64
```

```powershell
BrainFucker.exe .\bf_source\hello.bf -o hello-x86.exe --target win32-x86
```

```powershell
BrainFucker.exe .\bf_source\hello.bf -o hello.com --target msdos-com
```

```powershell
BrainFucker.exe .\bf_source\hello.bf -o hello-dos.exe --target msdos-exe
```

```powershell
BrainFucker.exe .\bf_source\hello.bf --run
```

```powershell
BrainFucker.exe --list-targets
```

To publish:
```powershell
dotnet publish -c Release
```

When built on Windows, MSBuild defines the GUI compile symbol automatically, so launching `BrainFucker.exe` without parameters opens the native file-picker GUI. Parameterized launches continue to use the CLI.

## Options

- `-o`, `--output`: desired output binary path
- `--run`: build to an OS temp directory, execute the generated binary, then remove it. This only works when the selected target can run on the current host OS.
- `--quiet-run`: with `--run`, suppress CLI status/success output so only the generated program output is shown
- `--list-targets`: list the available output targets and exit
- `--cells <count>`: tape size, defaults to `30000`
- `--target <id>`: output backend, currently `win32-x64`, `win32-x86`, `msdos-com`, or `msdos-exe`

## Target matrix

| Target | Output | Runtime family | `--run` win-x64 host | `--run` win-x86 host |
| --- | --- | --- | --- | --- |
| `win32-x64` | `.exe` | Windows PE x64 | ✅ | ❌ |
| `win32-x86` | `.exe` | Windows PE x86 | ✅ | ✅ |
| `msdos-com` | `.com` | MS-DOS 16-bit COM | ❌ | ❌ |
| `msdos-exe` | `.exe` | MS-DOS 16-bit EXE | ❌ | ❌ |

## MS-DOS target

The DOS targets emit 16-bit binaries intended for MS-DOS 5.0-style environments.

- Use DOSBox, FreeDOS, or real DOS hardware to run the generated binary
