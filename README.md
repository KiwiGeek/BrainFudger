# BrainFucker

`BrainFucker` is a small C# Brainfuck compiler with a pluggable binary emitter interface. The CLI is built with `System.CommandLine` and styled with `Spectre.Console`. It currently ships with Win32 x64, Win32 x86, and MS-DOS `.COM` backends.

## What it does

- Reads a Brainfuck source file
- Validates matching loop brackets
- Generates a valid PE executable with Win32 imports from `kernel32.dll`
- Uses `GetStdHandle`, `ReadFile`, `WriteFile`, and `ExitProcess` directly from the emitted executable
- Generates tape bounds checks so invalid pointer moves fail with an error
- Routes output through a target backend interface so additional binary formats can plug in cleanly

## Usage

```powershell
dotnet run -- .\bf_source\hello.bf -o hello.exe
```

```powershell
dotnet run -- .\bf_source\hello.bf -o hello.exe --target win32-x64
```

```powershell
dotnet run -- .\bf_source\hello.bf -o hello-x86.exe --target win32-x86
```

```powershell
dotnet run -- .\bf_source\hello.bf -o hello.com --target msdos-com
```

```powershell
dotnet run -- .\bf_source\hello.bf --run
```

```powershell
dotnet run -- .\bf_source\advanced-test.bf --run --quiet-run
```

```powershell
dotnet publish -c Release
```

`BrainFucker` does not rely on MSVC or another external C compiler. It writes the final `.exe` itself.
When you run `dotnet publish` without specifying `-r`, the project now defaults to the current host platform RID for Native AOT publishing.

## Options

- `-o`, `--output`: desired output binary path
- `--run`: build to an OS temp directory, execute the generated binary, then remove it. This only works when the selected target can run on the current host OS.
- `--quiet-run`: with `--run`, suppress CLI status/success output so only the generated program output is shown
- `--cells <count>`: tape size, defaults to `30000`
- `--target <id>`: output backend, currently `win32-x64`, `win32-x86`, or `msdos-com`

## MS-DOS target

The `msdos-com` target emits a 16-bit `.COM` program intended for MS-DOS 5.0-style environments.

- Prefer the `.com` extension for output files
- `--run` is intentionally blocked for this target on the current host
- Use DOSBox, FreeDOS, or real DOS hardware to run the generated binary
