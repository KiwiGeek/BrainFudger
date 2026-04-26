# BrainFudger

`BrainFudger` is a C# Brainf$#k compiler with pluggable binary emitters. It currently ships with Linux x64, Linux x86, Linux arm64, Win32 x64, Win32 x86, MS-DOS `.COM`, MS-DOS `MZ .EXE`, and macOS Apple Silicon Mach-O backends.

If you want the full hex-goblin tour, the new [docs index](./docs/README.md) walks through Brainf$#k itself plus the executable formats this project emits.

- the CLI remains the default interactive surface
- a native Win32 GUI host is compiled in on Windows
- a native AppKit GUI host is compiled in on macOS
- a native Linux desktop GUI workflow is compiled in on Linux hosts

## What it does

- reads a Brainf$#k source file
- validates matching loop brackets
- lowers source into a shared intermediate opcode stream before backend code generation
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
BrainFudger.exe .\bf_source\hello.bf -o hello-linux --target linux-x64
```

```powershell
BrainFudger.exe .\bf_source\hello.bf -o hello-linux-x86 --target linux-x86
```

```powershell
BrainFudger.exe .\bf_source\hello.bf -o hello-linux-arm64 --target linux-arm64
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

Alternate branding builds:

Use the MSBuild property `BrandingDirective` to select a non-default branding mode for a build or publish. The property takes one of two values:

- `IAMGARYPENN`: uncensors the public-facing branding and language name
- `ZEROCOOL`: switches the public-facing branding to `BrainFux0r` and `BrainFux`

Examples:

```powershell
dotnet build -p:BrandingDirective=IAMGARYPENN
```

```powershell
dotnet build -p:BrandingDirective=ZEROCOOL
```

The same property works with `dotnet publish`:

```powershell
dotnet publish -c Release -p:BrandingDirective=IAMGARYPENN
```

```powershell
dotnet publish -c Release -p:BrandingDirective=ZEROCOOL
```

## Options

- `-o`, `--output`: desired output binary path
- `--run`: build to an OS temp directory, execute the generated binary, then remove it. This only works when the selected target can run on the current host OS.
- `--quiet-run`: with `--run`, suppress CLI status/success output so only the generated program output is shown
- `--list-targets`: list the available output targets and exit
- `--cells <count>`: tape size, defaults to `30000`
- `--enable-random`: enable the `?` extension command for pseudorandom byte generation
- `--enable-clear`: enable the `!` extension command for best-effort terminal clearing
- `--enable-delay`: enable the `~` extension command for best-effort delay behavior
- `--target <id>`: output backend, currently `linux-arm64`, `linux-x64`, `linux-x86`, `win-x64`, `win-x86`, `msdos-com`, `msdos-exe`, or `osx-arm64`

Extension commands are disabled by default in both the CLI and GUI hosts. The GUI surfaces expose them as unchecked boxes.

## Compiler pipeline

The frontend now has three stages:

1. filter source down to significant tokens
2. validate loops and lower the program into a shared intermediate opcode stream
3. hand the intermediate program to the selected emitter

See [docs/intermediate-opcodes.md](./docs/intermediate-opcodes.md) for the opcode set and lowering rules.

## Target matrix

| Target | Output | Runtime family | `--run` win-x64 host | `--run` win-x86 host | `--run` linux x64 host | `--run` linux arm64 host | `--run` osx-arm64 host |
| --- | --- | --- | --- | --- | --- | --- | --- |
| `linux-arm64` | none | Linux ELF arm64 | ❌ | ❌ | ❌ | ✅ | ❌ |
| `linux-x64` | none | Linux ELF x64 | ❌ | ❌ | ✅ | ❌ | ❌ |
| `linux-x86` | none | Linux ELF x86 | ❌ | ❌ | ✅ | ❌ | ❌ |
| `win-x64` | `.exe` | Windows PE x64 | ✅ | ❌ | ❌ | ❌ | ❌ |
| `win-x86` | `.exe` | Windows PE x86 | ✅ | ✅ | ❌ | ❌ | ❌ |
| `msdos-com` | `.com` | MS-DOS 16-bit COM | ❌ | ❌ | ❌ | ❌ | ❌ |
| `msdos-exe` | `.exe` | MS-DOS 16-bit EXE | ❌ | ❌ | ❌ | ❌ | ❌ |
| `osx-arm64` | none | macOS Apple Silicon Mach-O | ❌ | ❌ | ❌ | ❌ | ✅ |

## Linux ELF targets

The Linux targets emit static syscall-only ELF binaries.

- `linux-x64`: raw x86-64 Linux syscalls
- `linux-x86`: raw 32-bit x86 Linux syscalls
- `linux-arm64`: raw AArch64 Linux syscalls
- `--run` currently works when the generated target can execute on the host kernel architecture
- generated files default to no extension
- See [docs/linux-elf.md](./docs/linux-elf.md) for the low-level format breakdown

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

## The Elephant

Yes, this is the part where a professional developer admits the README had an elephant in it.

The default build ships with censored public-facing wording. If you want to uncensor it at compile time, there are two opt-in switches:

- `ZEROCOOL`: swaps the public branding to `BrainFux0r` and the language name to `BrainFux`
- `IAMGARYPENN`: removes all censoring on the branding, and refers to the language by its official name. Extra points if you get the reference. If you google it, you did not get the reference.

Set them through the MSBuild property `BrandingDirective`, for example `dotnet build -p:BrandingDirective=IAMGARYPENN`.

Do not enable both at once.
