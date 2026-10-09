# claude-swap-wrapper

A tiny launcher that lets the Claude Code **VS Code extension** run through
[claude-swap](https://github.com/realiti4/claude-swap), so each workspace uses the account mapped to its folder
(`claude-swap map <account> <folder>`).

> **Unofficial.** A community tool, not affiliated with or endorsed by Anthropic or the author of claude-swap.
> "Claude" is a trademark of Anthropic. Windows only. It relies on the extension's documented
> `claudeCode.claudeProcessWrapper` setting and on behavior observed in specific versions (see [FINDINGS.md](FINDINGS.md)),
> which may change.

## How it works

The extension's `claudeCode.claudeProcessWrapper` setting starts this exe instead of Claude, with the path to its
bundled `claude.exe` as the first argument. The wrapper:

1. drops that first argument. claude-swap resolves `claude` from PATH itself and can't be pointed at a specific binary.
2. runs `cswap run -- <remaining args>`, so claude-swap picks the account from the current folder's mapping.
3. sets `CLAUDE_CONFIG_DIR=unused-forces-session-mode` to **always use the account's session profile**
   (`~/.claude-swap-backup/sessions/<n>-<email>`).

### Unmapped folders

In a folder with no mapping (resolved like claude-swap does it: case-folded, most specific mapped parent wins),
the wrapper starts the extension's bundled `claude.exe` directly with no `CLAUDE_CONFIG_DIR`. The effect is the
default login and `~/.claude`, the same as having no wrapper. Going through `cswap run` there would launch plain
`claude` with the placeholder `CLAUDE_CONFIG_DIR` still set, and Claude would create a real
`unused-forces-session-mode` config folder inside the project.

### `claude auth status` output

The extension runs `claude auth status --json` through the wrapper. Through `cswap run`, cswap's own status lines
("Launching Account-1 …") share stdout. Python holds them back when stdout is a pipe, so they come out right after
the JSON and break the extension's parser (`claude auth status parse failed`, repeated every few seconds). For that
one call, the wrapper passes only the JSON document to stdout and moves everything else to stderr.

This does **not** make the extension's conversation list follow the session profile. With a process wrapper
configured, the extension keeps listing `~/.claude/projects` regardless of the `configDirectory` that call reports.
See [FINDINGS.md](FINDINGS.md#4-the-vs-code-history-list-ignores-the-wrappers-config-folder).

### History list (`~/.claude/projects` junctions)

The extension's conversation list always reads `~/.claude/projects/<folder>`, but mapped sessions are saved in the
account's session profile. So on every launch in a mapped folder, the wrapper creates a **junction** in
`~/.claude/projects` for each of this project's history folders in the session profile that isn't there yet: the
project's own folder and its worktrees' (`<folder>--claude-worktrees-<name>`). After that, the list shows them.

- It only adds links. It never moves, merges or deletes anything, and an existing folder or link with that name is
  left alone. If `~/.claude` already has real history for the project, merge it into the session profile and replace
  it by hand (see [FINDINGS.md](FINDINGS.md#one-time-merge-of-existing-history)).
- A new worktree's history folder only exists once Claude has saved to it, so it's linked on the **next** launch in
  that project (another chat, a window reload, or `CSWAP_WRAPPER_LINK_ONLY=1`).
- Each created link is logged (`linked history: …`) in `wrapper.log`.

**Chats that moved into a worktree.** When a chat switches into a git worktree, Claude moves its transcript into that
worktree's history folder. The project window's list never looks there: it only reads the project's own history
folder. So when the wrapper is launched from the project's own folder, it also adds, for every chat in the project's
worktree history folders:

- a **hard link** of the transcript in the project's main history folder. It's the same file under a second name,
  so it shows in the list, its bookmarks resolve, and new messages appear under both names.
- a **junction** for the chat's details folder (`<id>\`: subagents, tool results).

This relies on Claude appending to transcripts in place and moving them by rename, which keeps the file's identity.
That was verified on 2026-10-05: the NTFS file ID stayed the same while the file grew. An existing file with the same
name in the main folder is never overwritten (it's logged and skipped).
- **Never delete these links recursively** (`Remove-Item -Recurse`, `rm -rf`): that can follow the link and delete
  the real history. Remove only the link: `cmd /c rmdir "<link>"`, or `[IO.Directory]::Delete("<link>", $false)`.

### Diagnostics

- Dry run (prints the route and the history links it would create, changes and launches nothing):
  ```bash
  CSWAP_WRAPPER_DRYRUN=1 claude-swap-wrapper.exe <path-to-claude.exe>
  ```
- Link history only, launching nothing (run from the project folder):
  ```bash
  CSWAP_WRAPPER_LINK_ONLY=1 claude-swap-wrapper.exe <path-to-claude.exe>
  ```
- Every launch is logged (cwd, route, arguments) to `%LOCALAPPDATA%\claude-swap-wrapper\wrapper.log`, rotated at
  ~1 MB. Prompts go over stdin and aren't logged.

### Why step 3 matters

Without it, claude-swap has a shortcut. When the mapped account happens to be the current default login at launch,
it runs plain Claude on `~/.claude`. Otherwise it uses the session profile. So one workspace's conversations would
be saved in two places depending on which account was the default at the time, and old conversations could fail to
resume. Setting `CLAUDE_CONFIG_DIR` makes claude-swap skip the shortcut. The placeholder value is never read, only
checked for presence, and then replaced with the session profile path.

**Trade-off:** when the mapped account is *also* the default login, that account has two live credential copies
(`~/.claude` and its session profile). They share one single-use refresh token, so whichever renews first
invalidates the other. Keep a different account as the default login than the ones your mapped workspaces use.
[Claude Swap Desktop](https://github.com/koenigstag/claude-swap-desktop) warns when they overlap.

## Build and install

Requires the .NET 6 SDK.

```bash
dotnet publish -c Release -o publish
```

Copy the contents of `publish/` to `%USERPROFILE%\.local\bin\claude-swap-wrapper\`, then in VS Code user settings:

```json
"claudeCode.claudeProcessWrapper": "C:\\Users\\<you>\\.local\\bin\\claude-swap-wrapper\\claude-swap-wrapper.exe"
```

Reload VS Code windows after replacing the exe. While VS Code is running the wrapper its files are locked. Rename
them first (Windows allows renaming a running exe), then copy the new ones in.

Keep the default login (`claude-swap switch`) on an account that no mapped folder uses. See
[FINDINGS.md](FINDINGS.md) for why.

## History

Written 2026-08-21. An earlier `.cmd` version (`shift` + `cswap run -- %*`) failed because the extension can't spawn
`.cmd` files (`spawn EINVAL`).

## License

[MIT](LICENSE)
