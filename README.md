# Claude Code Tool Window for Visual Studio (Unofficial)

Runs the [Claude Code](https://code.claude.com/docs/en/overview) CLI inside a dockable tool window in
Visual Studio 2022 and Visual Studio 2026, started in the folder of the open solution.

This is a community project. It is not affiliated with or endorsed by Anthropic.

## First run

If the Claude Code CLI is not installed, the window says so and offers an **Install Claude Code**
button that runs the official installer (`irm https://claude.ai/install.ps1 | iex`) inside the
window and then starts Claude Code, which asks you to sign in.

## Requirements

- Visual Studio 2022 (17.x) or Visual Studio 2026 (18.x), any edition
- The Claude Code CLI. The extension finds it on `PATH` or in its usual install folders,
  and offers to install it when it is missing

## Usage

1. **View > Other Windows > Claude Code** opens the tool window.
2. The CLI starts in the solution folder (or your user folder when no solution is open).
3. **New chat** starts a fresh session, for example after opening another solution.
   **History** lists the previous chats of that folder; click one to resume it.
   **Fix errors** types the errors from the Error List into the prompt for Claude to fix.
   The button at the right end of the toolbar opens another window with a chat of its own.
4. Right-click in the code editor > **Claude Code: Reference This File/Selection** types an
   `@path/to/file#10-25` reference into the prompt, so Claude knows which code you mean.
   The default shortcut is **Ctrl+Alt+K**; change it in Tools > Options > Environment > Keyboard.
5. **Tools > Options > Claude Code > General** lets you change the command (full path to the CLI)
   and pass extra arguments such as `--continue`.

## Git

Claude Code works with Git on its own from the embedded session: ask it to review the pending
changes, create a branch, write a commit, resolve a merge conflict or (with the GitHub CLI
installed) open a pull request. Files it edits are reloaded by Visual Studio, and the
**Git Changes** window shows the diff of everything it touched before you commit.

## Building

1. Install the *Visual Studio extension development* workload.
2. Open `ClaudeCodeVS.sln` and press **F5**. An experimental instance of Visual Studio starts
   with the extension loaded.
3. A Release build produces `src\ClaudeCodeVS\bin\Release\ClaudeCodeVS.vsix`, which installs by
   double-click.

## How it works

The tool window hosts a real Windows console. The extension launches
`conhost.exe cmd.exe /s /k "claude"`, then re-parents the console window into the tool window.
The process tree is placed in a job object, so the console and the CLI are terminated when the
session is restarted or Visual Studio exits.

| File | Role |
| --- | --- |
| `ClaudeCodePackage.cs` | Package, menu command, registrations |
| `ToolWindow/ClaudeToolWindow.cs` | The tool window pane |
| `ToolWindow/ClaudeTerminalControl.cs` | Toolbar, working folder and command resolution |
| `Terminal/ConsoleHost.cs` | Console process and window embedding |
| `Terminal/KillOnCloseJob.cs` | Job object that cleans up the process tree |
| `ToolWindow/SessionHistory.cs` | Reads previous chats from the Claude Code transcripts |
| `ToolWindow/ToolbarUi.cs` | Theme-aware toolbar buttons |
| `Options/GeneralOptions.cs` | Options page |

## Known limitations

- The console receives keyboard focus only when clicked, and Visual Studio shortcuts do not
  reach it.
- Console colors follow the Visual Studio theme only for the default background and text;
  switch the Claude Code theme with `/theme` if text is hard to read.
- When another solution is opened, the window offers a new chat there; the running chat is
  kept until you accept.
- **Fix errors** sends at most 15 errors, as text; Claude does not see the Error List live,
  nor the debugger state.
- File paths containing spaces may not resolve as `@` references.

## Roadmap

- Replace the re-parented console with a ConPTY-based terminal (keyboard focus, no flicker)
- Native diff review of proposed edits before they are written to disk
- An MCP server exposing the Error List, open documents and build results to Claude

## License

MIT. See `src/ClaudeCodeVS/LICENSE.txt`.
