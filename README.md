# shrun

A terminal-based workflow runner for Windows. Define commands in a config file, select and execute them interactively.

## Features

- Interactive TUI with real-time search and multi-select
- Save and reuse command combinations as workflows
- Combine multiple commands into a single alias
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
    └── default/        # Project folder (one per project)
        ├── config.json # Command definitions (JSON)
        ├── config.tsv  # Command definitions (TSV, alternative to JSON)
        ├── workflows.json  # Saved workflows (auto-generated, not committed)
        └── aliases.json    # Saved aliases (auto-generated, not committed)
```

## Requirements

- Windows
- [.NET 10.0 SDK](https://dotnet.microsoft.com/download) (to build from source)
- [.NET 10.0 Runtime](https://dotnet.microsoft.com/download) (to run the exe only)

## Setup

1. Place `shrun.exe` anywhere you like
2. Create a `projects/` folder **in the same directory as the exe**
3. Create a subfolder for each project and add `config.json` or `config.tsv` inside it

```
any-folder/
├── shrun.exe
└── projects/
    ├── projectA/
    │   └── config.json
    └── projectB/
        └── config.tsv
```

If multiple projects exist, shrun shows a selection screen on startup. Use **Switch config** from the main menu to switch projects at any time.

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

## config.tsv

Tab-separated alternative to `config.json`. If both files exist, `config.json` takes priority.

```
name	group	dir	cmd	shell
task-a1	prepare	Z:\task-a1	echo task-a1 done
task-a2	prepare	Z:\task-a2	echo task-a2 done
task-b1	process	Z:\task-b1	echo task-b1 done
task-b2	process	Z:\task-b2	echo task-b2 done
task-c1	deploy		echo task-c1 done
task-c2	deploy		echo task-c2 done
```

## Config fields

| Field   | Required | Description |
|---------|----------|-------------|
| `name`  | Yes      | Command name |
| `group` | No       | Group label for filtering |
| `dir`   | No       | Working directory (leave empty to use current) |
| `cmd`   | Yes      | Command to execute |
| `shell` | No       | `"ps"` for PowerShell, omit for cmd.exe |

## Usage

Run `shrun.exe` from the terminal. Use arrow keys to navigate.

```
  ┌───────────────┐
  │  S H R U N    │
  └───────────────┘

  > Run workflow
    Run manually
    Create workflow
    Edit workflow
    Delete workflow
    Manage aliases
    Switch config
    Exit
```

### Selecting commands

- `↑↓` — move cursor
- `Space` — toggle selection
- `Enter` — confirm
- `Esc` — cancel / go back
- Type to search by name or group
- `Tab` — switch between search fields (`/` and `Group /`)

### Workflows

Workflows are saved combinations of commands. Create one via **Create workflow**, then run it instantly from **Run workflow**.

### Aliases

Aliases combine multiple commands into a single selectable item. Create one via **Manage aliases → Create alias**.

In the command selection screen, aliases appear with an `@` prefix and `[alias]` group tag:

```
  [ ] @ clean-build  [alias]  clean > build
```

Selecting an alias and running it expands to its component commands at execution time.

Aliases are stored in `aliases.json` (per project, not committed to git).

## Working Directory

The `dir` field accepts any absolute path:

```json
{ "name": "task-a1", "dir": "C:\\Users\\you\\project\\task-a1", "cmd": "echo done" }
```

**Optional: Virtual Drive**

If your project paths vary by environment, you can use `subst` to map a fixed drive letter:

```bat
subst Z: "C:\path\to\your\project"
```

Then use `Z:\` paths in config. This keeps config portable across machines. A sample batch file `test_drive.bat` is included for testing.

## License

MIT License — see [LICENSE](LICENSE) for details.

## Building from source

```
dotnet publish -c Release
```

Output: `bin/publish/shrun.exe`
