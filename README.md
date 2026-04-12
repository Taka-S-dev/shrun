# shrun

A terminal-based workflow runner for Windows. Define commands in a JSON file, select and execute them interactively.

## Features

- Interactive TUI with real-time search and multi-select
- Save and reuse command combinations as workflows
- Supports both `cmd.exe` and PowerShell per command
- Remembers the last used workflow
- Scroll support for large command lists

## File Structure

```
shrun/
├── Program.cs          # Main logic
├── shrun.csproj        # .NET project file
├── README.md
├── test_drive.bat      # Creates a virtual drive (Z:) for testing
└── projects/
    └── config.json     # Command definitions
```

## Requirements

- Windows
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) (to build from source)
- [.NET 10.0 Runtime](https://dotnet.microsoft.com/download) (to run the exe only)

## Setup

1. Place `shrun.exe` anywhere you like
2. Create a `projects/` folder **in the same directory as the exe**
3. Add `projects/config.json` (see below)

```
any-folder/
├── shrun.exe
└── projects/
    └── config.json
```

## config.json

```json
{
  "commands": [
    { "name": "task-a1", "group": "prepare", "dir": "Z:\\task-a1", "cmd": "echo task-a1 done" },
    { "name": "task-a2", "group": "prepare", "dir": "Z:\\task-a2", "cmd": "echo task-a2 done" },
    { "name": "task-b1", "group": "process", "dir": "Z:\\task-b1", "cmd": "echo task-b1 done" },
    { "name": "task-b2", "group": "process", "dir": "Z:\\task-b2", "cmd": "echo task-b2 done" },
    { "name": "task-c1", "group": "deploy",  "dir": "",            "cmd": "echo task-c1 done" },
    { "name": "task-c2", "group": "deploy",  "dir": "",            "cmd": "echo task-c2 done" }
  ]
}
```

| Field   | Required | Description |
|---------|----------|-------------|
| `name`  | Yes      | Command name |
| `group` | Yes      | Group label for filtering |
| `dir`   | No       | Working directory (leave empty to use current) |
| `cmd`   | Yes      | Command to execute |
| `shell` | No       | `"ps"` for PowerShell, omit for cmd.exe |

## Usage

Run `shrun.exe` from the terminal. Use arrow keys to navigate.

```
  +---------------------------------+
  |            SHRUN                |
  +---------------------------------+

  > Run workflow
    Run manually
    Create workflow
    Edit workflow
    Delete workflow
    Exit
```

### Selecting commands

- `↑↓` — move cursor
- `Space` — toggle selection
- `Enter` — confirm
- `Esc` — cancel / go back
- Type to search by name or group
- `g:` prefix to filter by group only

### Workflows

Workflows are saved combinations of commands. Create one via **Create workflow**, then run it instantly from **Run workflow**.

## Virtual Drive

If your project paths vary by environment, use `subst` to map a fixed drive letter to your working directory before running shrun:

```bat
subst Z: "C:\path\to\your\project"
```

Then use `Z:\` paths in `config.json`. A sample batch file `test_drive.bat` is included for testing.

## License

MIT License — see [LICENSE](LICENSE) for details.

## Building from source

```
dotnet publish -c Release
```

Output: `bin/Release/net10.0/win-x64/publish/shrun.exe`
