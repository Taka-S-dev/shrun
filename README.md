# shrun

A terminal-based workflow runner for Windows. Define commands in a config file, select and execute them interactively.

## Features

- Interactive TUI with real-time search and multi-select
- Save and reuse command combinations as workflows
- Combine multiple commands into a single alias
- `{placeholder}` substitution — pick values from a selection list at runtime
- Optional `vars` column to map slot names to named lists
- Supports both `cmd.exe` and PowerShell per command
- Remembers the last used workflow
- Retry from the failed step when a command fails

## File Structure

```
shrun/
├── Program.cs          # Entry point and main flow
├── Models.cs           # Data model records
├── Tui.cs              # Terminal UI primitives
├── Selectors.cs        # Interactive selection screens
├── VarSystem.cs        # Selection list loading and slot resolution
├── ConfigStore.cs      # Config loading and project management
├── Runner.cs           # Command execution
├── shrun.csproj        # .NET project file
├── README.md
├── test_drive.bat      # Creates a virtual drive (Z:) for testing
└── projects/
    └── default/        # Project folder (one per project)
        ├── config.json # Command definitions (JSON)
        ├── config.tsv  # Command definitions (TSV, alternative to JSON)
        ├── lists/      # Selection lists (one .tsv per list)
        │   ├── project.tsv  # Sample list for {project}, {projDir}, {projCmd}
        │   └── env.tsv      # Sample list for {env}
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
    { "name": "build",  "group": "make",   "dir": "{projDir}", "cmd": "echo building {projCmd}", "vars": { "projDir": "project", "projCmd": "project" } },
    { "name": "test",   "group": "make",   "dir": "{project}", "cmd": "echo testing {project}" },
    { "name": "deploy", "group": "deploy", "dir": "",          "cmd": "echo deploying {env}" }
  ]
}
```

## config.tsv

Tab-separated alternative to `config.json`. If both files exist, `config.json` takes priority.

```
name	group	dir	cmd	shell	vars
build	make	{projDir}	echo building {projCmd}		projDir=project,projCmd=project
test	make	{project}	echo testing {project}
deploy	deploy		echo deploying {env}
```

## Config fields

| Field   | Required | Description |
|---------|----------|-------------|
| `name`  | Yes      | Command name |
| `group` | No       | Group label for filtering |
| `dir`   | No       | Working directory (leave empty to use current). Supports `{placeholders}` |
| `cmd`   | Yes      | Command to execute. Supports `{placeholders}` |
| `shell` | No       | `"ps"` for PowerShell, omit for cmd.exe |
| `vars`  | No       | Maps slot names to list names (see below) |

## Placeholders and Selection Lists

Use `{name}` placeholders in `cmd` or `dir` to prompt for a value at runtime.

### Selection lists

Create lists via **Manage lists** from the main menu. Each list is stored as a `.tsv` file in `projects/<name>/lists/`:

```
Z:\api    api
Z:\web    web
Z:\worker worker
```

By default, `{name}` selects from the list named `name`. Each occurrence of the same placeholder is prompted independently.

### vars — mapping slot names to lists

If you want to use different slot names in `dir` and `cmd` but have them select from the same list, use the `vars` field:

```json
{ "dir": "{projDir}", "cmd": "echo building {projCmd}", "vars": { "projDir": "project", "projCmd": "project" } }
```

Both `{projDir}` and `{projCmd}` will select from the `project` list, but are treated as separate variables — once a value is selected for each, it is reused consistently throughout the command.

### Using placeholders in workflows and aliases

When creating a workflow or alias, shrun prompts you to pick a value for each placeholder. The selected values are saved so you don't need to re-enter them at run time.

### Using placeholders in Run manually

When running commands manually, shrun prompts for each placeholder before execution.

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
    Manage lists
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

Workflows are saved combinations of commands with pre-set placeholder values. Create one via **Create workflow**, then run it instantly from **Run workflow**.

### Aliases

Aliases combine multiple commands into a single selectable item. Create one via **Manage aliases → Create alias**.

In the command selection screen, aliases appear with an `@` prefix and `[alias]` group tag:

```
  [ ] @ clean-build  [alias]  clean > build
```

Selecting an alias and running it expands to its component commands at execution time.

### Retry on failure

When a command fails during execution, shrun offers a recovery menu:

```
  Error: build failed.

> Retry from step 2  (build → test → deploy)
  Retry all
  Abort
```

The header shows `(retry N)` on each subsequent attempt so you can tell the retry is running.

## Working Directory

The `dir` field accepts any absolute path or a `{placeholder}`:

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
