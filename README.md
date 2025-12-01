# SecureDelete

SecureDelete is a lightweight Windows utility for securely deleting files and folders. It overwrites file contents multiple times with cryptographically strong random data before removing the file, and can be invoked from a context menu entry in File Explorer.

## Features
- Multi-pass overwriting (configurable; default is 3 passes).
- Console progress bar shows wipe completion as files are processed, with a windowed progress dialog when invoked from File Explorer.
- Recursive wiping of directories when requested.
- Removes read-only attributes automatically and optionally continues after non-critical errors with `--force`.
- Designed for seamless File Explorer integration via a simple registry entry.

## Build instructions (Visual Studio)
1. Open `SecureDelete.sln` in Visual Studio 2026 (or Visual Studio 2022/2025).
2. Ensure the project targets the .NET 8.0 SDK for Windows (`net8.0-windows`) or newer (you can retarget in project properties if needed).
3. Build the solution in **Release** mode to produce `SecureDelete.exe` under `src/SecureDelete.App/bin/Release/net8.0-windows/`.

## Usage
```
SecureDelete.exe [options] <fileOrDirectory> [additional targets...]

Options:
  -p, --passes <number>    Number of overwrite passes (default: 3)
  -r, --recursive          Recursively wipe directories
  -f, --force              Ignore read-only attributes and continue on errors
      --window-ui          Show a windowed progress indicator (used by File Explorer context menu)
  -h, --help               Show help text
```

Examples:
- `SecureDelete.exe --passes 5 --recursive C:\\Sensitive\\Archive`
- `SecureDelete.exe -p 2 C:\\Temp\\file.txt D:\\logs\\old.log`
- `SecureDelete.exe --window-ui "%1"` (used by the context menu invocation)

## File Explorer integration
You can add a context menu entry to File Explorer so that right-clicking a file or folder allows invoking SecureDelete directly.

1. Build and place `SecureDelete.exe` somewhere stable, e.g. `C:\\Program Files\\SecureDelete\\SecureDelete.exe`.
2. Update `ExplorerIntegration.reg` to point to the actual executable path if you choose a different location.
3. Double-click `ExplorerIntegration.reg` (or import via `regedit`) to add the menu entry.
4. Right-click a file or folder and choose **Secure Delete** to securely wipe the selection.

### Removing the context menu entry
Delete the following registry keys:
- `HKEY_CLASSES_ROOT\\*\\shell\\SecureDelete`
- `HKEY_CLASSES_ROOT\\Directory\\shell\\SecureDelete`

## Notes on secure deletion
- HDDs: multiple overwrite passes combined with a rename-and-delete workflow provide strong protection for traditional spinning disks.
- SSDs: due to wear leveling, TRIM, and controller remapping, overwriting cannot guarantee destruction of every copy of the data. Pair this tool with full-disk encryption and, when needed, the drive's built-in **secure erase** or **sanitize** feature.
- Closing applications that may hold file locks will improve wipe reliability.
- Increase the number of passes for highly sensitive data, but note that more passes take longer.
