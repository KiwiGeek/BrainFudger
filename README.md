# BrainFudger

`BrainFudger` is a C# Brainfuck compiler with pluggable binary emitters. It currently ships with Win32 x64, Win32 x86, MS-DOS `.COM`, MS-DOS `MZ .EXE`, and macOS Apple Silicon Mach-O backends.

If you want the full hex-goblin tour, the new [docs index](./docs/README.md) walks through Brainfuck itself plus the executable formats this project emits.

## What it does

- reads a Brainfuck source file
- validates matching loop brackets
- generates a valid native executable
- generates tape bounds checks so invalid pointer moves fail with an error

## Usage

```powershell
BrainFudger.exe .\bf_source\hello.bf -o hello.exe
```

```powershell
BrainFudger.exe .\bf_source\hello.bf -o hello.exe --target win-x64
```

```powershell
BrainFudger.exe .\bf_source\hello.bf -o hello-x86.exe --target win-x86
```

```powershell
BrainFudger.exe .\bf_source\hello.bf -o hello.com --target msdos-com
```

```powershell
BrainFudger.exe .\bf_source\hello.bf -o hello-dos.exe --target msdos-exe
```

```powershell
BrainFudger.exe .\bf_source\hello.bf -o hello.macho --target osx-arm64
```

```powershell
BrainFudger.exe .\bf_source\hello.bf --run
```

```powershell
BrainFudger.exe --list-targets
```

To publish:

```powershell
dotnet publish -c Release
```

When built on Windows, MSBuild defines the GUI compile symbol automatically, so launching `BrainFudger.exe` without parameters opens the native file-picker GUI. Parameterized launches continue to use the CLI.

## Options

- `-o`, `--output`: desired output binary path
- `--run`: build to an OS temp directory, execute the generated binary, then remove it. This only works when the selected target can run on the current host OS.
- `--quiet-run`: with `--run`, suppress CLI status/success output so only the generated program output is shown
- `--list-targets`: list the available output targets and exit
- `--cells <count>`: tape size, defaults to `30000`
- `--target <id>`: output backend, currently `win-x64`, `win-x86`, `msdos-com`, `msdos-exe`, or `osx-arm64`

## Target matrix

| Target | Output | Runtime family | `--run` win-x64 host | `--run` win-x86 host | `--run` osx-arm64 host | 
| --- | --- | --- | --- | --- |--- |
| `win-x64` | `.exe` | Windows PE x64 | ✅ | ❌ | ❌ |
| `win-x86` | `.exe` | Windows PE x86 | ✅ | ✅ | ❌ |
| `msdos-com` | `.com` | MS-DOS 16-bit COM | ❌ | ❌ | ❌ |
| `msdos-exe` | `.exe` | MS-DOS 16-bit EXE | ❌ | ❌ | ❌ |
| `osx-arm64` | `.macho` | macOS Apple Silicon Mach-O | ❌ | ❌ | ✅ |

## MS-DOS targets

The DOS targets emit 16-bit binaries intended for MS-DOS 5.0-style environments.

- Use DOSBox, FreeDOS, or real DOS hardware to run the generated binary
- See [docs/msdos-com.md](./docs/msdos-com.md) and [docs/msdos-mz-exe.md](./docs/msdos-mz-exe.md) for the low-level format details

## macOS Apple Silicon target

The `osx-arm64` target emits a 64-bit Mach-O executable for Apple Silicon Macs.

- `--run` is only allowed on macOS arm64 hosts
- the generated file defaults to a `.macho` extension, but you can name it however you like
- future work will likely add `osx-x64` and a universal/fat binary wrapper
- See [docs/mach-o-arm64.md](./docs/mach-o-arm64.md) for the low-level format breakdown
