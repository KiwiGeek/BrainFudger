# BrainFudger

`BrainFudger` is a C# Brainfuck compiler with pluggable binary emitters. It currently ships with Win32 x64, Win32 x86, MS-DOS `.COM`, MS-DOS `MZ .EXE`, and macOS Apple Silicon Mach-O backends.

If you want the full hex-goblin tour, the new [docs index](./docs/README.md) walks through Brainfuck itself plus the executable formats this project emits.

The app stays as a single project, with compiler-gated platform GUI hosts:

- the CLI remains the default interactive surface
- a native Win32 GUI host is compiled in on Windows
- a native AppKit GUI host is compiled in on macOS

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
BrainFudger.exe .\bf_source\hello.bf -o hello --target osx-arm64
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

## Releases

Release notes live in [docs/releasing.md](./docs/releasing.md).

Short version:

- use semantic tags like `v1.2.3`, `v1.3.0-alpha.1`, or `v2.0.0-rc.1`
- only `v*` tags trigger the GitHub release workflow
- `bugfix` bumps patch, `feature` bumps minor, `breaking` bumps major
- prerelease tags become GitHub prereleases automatically
- release builds include commit-count and short-SHA metadata in `InformationalVersion`
- use the **Cut Release Tag** workflow in GitHub Actions to calculate and push the next tag

When built on Windows, MSBuild defines the GUI compile symbol automatically, so launching `BrainFudger.exe` without parameters opens the native file-picker GUI. Parameterized launches continue to use the CLI.

On macOS, the AppKit host provides a native window with source/output pickers, target selection, cells input, and Build/Run buttons wired through the same compilation workflow as the CLI.

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
| `osx-arm64` | none | macOS Apple Silicon Mach-O | ❌ | ❌ | ✅ |

## MS-DOS targets

The DOS targets emit 16-bit binaries intended for MS-DOS 5.0-style environments.

- Use DOSBox, FreeDOS, or real DOS hardware to run the generated binary
- See [docs/msdos-com.md](./docs/msdos-com.md) and [docs/msdos-mz-exe.md](./docs/msdos-mz-exe.md) for the low-level format details

## macOS Apple Silicon target

The `osx-arm64` target emits a 64-bit Mach-O executable for Apple Silicon Macs.

- `--run` is only allowed on macOS arm64 hosts
- the generated file defaults to no extension, but you can still provide any output name you like
- future work will likely add `osx-x64` and a universal/fat binary wrapper
- See [docs/mach-o-arm64.md](./docs/mach-o-arm64.md) for the low-level format breakdown
