# shrun

A terminal-based workflow runner for Windows. Define commands in a config file, select and execute them interactively.

## Features

- Interactive TUI with real-time search and multi-select
- Save and reuse command combinations as workflows
- Combine multiple commands into a single alias
- Variable substitution via `{varName}` placeholders — pick values at runtime from a managed list
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
        ├── vars/       # Variable definitions (one .tsv per variable)
        │   └── env.tsv
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
    { "name": "build",   "group": "make",   "dir": "{project}", "cmd": "echo building {project}" },
    { "name": "test",    "group": "make",   "dir": "{project}", "cmd": "echo testing {project}" },
    { "name": "deploy",  "group": "deploy", "dir": "",          "cmd": "echo deploying {env}" }
  ]
}
```

## config.tsv

Tab-separated alternative to `config.json`. If both files exist, `config.json` takes priority.

```
name	group	dir	cmd	shell
build	make	{project}	echo building {project}
test	make	{project}	echo testing {project}
deploy	deploy		echo deploying {env}
```

## Config fields

| Field   | Required | Description |
|---------|----------|-------------|
| `name`  | Yes      | Command name |
| `group` | No       | Group label for filtering |
| `dir`   | No       | Working directory (leave empty to use current). Supports `{varName}` |
| `cmd`   | Yes      | Command to execute. Supports `{varName}` |
| `shell` | No       | `"ps"` for PowerShell, omit for cmd.exe |

## Variables

Use `{varName}` placeholders in `cmd` or `dir` to prompt for a value at runtime.

### Defining variables

Open **Manage vars** from the main menu to create and edit variable lists. Each variable is stored as a `.tsv` file in `projects/<name>/vars/`.

```
value       label (optional)
dev         development
stg         staging
prd         production
```

### Using variables in workflows and aliases

When creating a workflow or alias that contains commands with `{varName}` placeholders, shrun prompts you to pick a value for each variable. The selected values are saved with the workflow/alias so you don't need to re-enter them at run time.

### Using variables in Run manually

When running commands manually, shrun prompts for any unresolved `{varName}` values before execution. The same value is reused across all commands that share the same variable name.

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
    Manage vars
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

The `dir` field accepts any absolute path or a `{varName}` placeholder:

```json
{ "name": "build", "dir": "{project}", "cmd": "echo building" }
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
