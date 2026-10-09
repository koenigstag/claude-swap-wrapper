# Findings: claude-swap + the VS Code wrapper

Notes from debugging "token expired" errors and missing conversation history in a setup that uses this wrapper
(late September – early October 2026). Each finding says what was observed, what caused it, and what (if anything)
was changed. Items marked *inference* weren't confirmed in source code.

Versions at the time: claude-swap 0.26.0, Claude Code / VS Code extension 2.1.285–2.1.289
(`anthropic.claude-code`), Windows 11.

## Setup

| Piece | Role |
|---|---|
| `~/.claude` | Claude Code's default config: the **default login** (`claude-swap switch` changes which account it holds), history in `projects/`. Used by the Claude desktop app, plain `claude` in terminals, and the VS Code extension's own UI. |
| `~/.claude-swap-backup/sessions/<n>-<email>/` | claude-swap **session profile**: a full Claude config folder per account, used by `claude-swap run <n>`. It has its own `.credentials.json` and its own `projects/` history. |
| `~/.claude-swap-backup/credentials/.creds-<n>-<email>.enc` | claude-swap's **stored backup** of each account's credentials. |
| `~/.claude-swap-backup/mappings.json` | Folder → account mappings (`claude-swap map`). |
| This wrapper | Set as `claudeCode.claudeProcessWrapper`. It routes the extension's Claude processes through `cswap run`. |

Two accounts were in use: a **work account**, mapped to the work folders, and a **personal account**, which at
first was also mapped to a parent folder of everything.

## 1. One account in two credential files breaks its refresh token

**Symptom:** "token expired" for one account, later for the other. `claude-swap list --token-status` showed a
credential copy with `expired, refresh token no`. Claude Code wipes a credential file's tokens when a refresh is
rejected.

**Cause:** Claude OAuth refresh tokens can only be used once. When an account is **both** the default login (in
`~/.claude/.credentials.json`) **and** run through its session profile (its own `.credentials.json`), two files
hold the same refresh token. The first to renew uses it up, and the other's next renewal is rejected and wiped.

**Evidence** (`~/.claude-swap-backup/claude-swap.log` + file timestamps), twice on the same day:

- The work account became the default login at 14:20. At 14:30:22 claude-swap bootstrapped its session profile
  from the stored backup and renewed the token (new expiry 22:30). At 14:32:10 `~/.claude/.credentials.json` was wiped.
- At 15:55:13 a `cswap run` in a folder mapped to the personal account (the default login at the time)
  bootstrapped that account's session profile and renewed. At 15:55:26 `~/.claude` was wiped.
- The work account's original break fits the same pattern: it was added while it was the default login, and its
  backup was never refreshed afterwards.

**Why the wrapper makes this reachable:** claude-swap has a guard for it. Its README says running the account
that is already the default login "launches plain `claude` on that login instead of a session (a second copy of the
active credential would go stale)". The wrapper's `CLAUDE_CONFIG_DIR=unused-forces-session-mode` switches that
guard off on purpose, to keep each workspace's history in one place (see the README's "Why step 3 matters").

*Inference:* claude-swap renews the token right when it bootstraps a session profile (both incidents show a fresh
8-hour expiry at bootstrap time). That is what uses up the token the default login still holds.

**Resolution:** keep the default login on an account that **no mapped folder uses**. Here: the personal account
is the default login and is no longer mapped; only the work folders are mapped, to the work account.
[Claude Swap Desktop](https://github.com/koenigstag/claude-swap-desktop) warns when the default-login account is
also mapped.

**Recovery when it happens:** the valid token usually survives in the session profile and/or the stored backup.

- `claude-swap switch <n> --force` copies the stored backup into `~/.claude` without first backing up the broken
  live login.
- Or sign in again (`claude auth login --email …`, then `claude-swap add`). Claude Swap Desktop's Re-login does
  this and checks that the right account signed in.

Don't run `claude-swap add` while the live login is broken: it saves the broken credential over the good backup
(it prints "could not verify … the access token is expired" and saves it anyway).

## 2. Unmapped folders leaked the placeholder `CLAUDE_CONFIG_DIR`

**Symptom:** a folder literally named `unused-forces-session-mode` appeared inside working folders (the home folder,
with a separate login and one conversation, and later a parent workspace folder).

**Cause:** in a folder with no mapping, `cswap run` "just launches plain `claude` with your default login"
(claude-swap README). It leaves `CLAUDE_CONFIG_DIR` as it found it, so Claude received the placeholder and treated it
as a relative config folder. It then asked for a fresh login and kept a separate history there.

**Fix (in this wrapper):** the wrapper resolves the mapping itself, the same way claude-swap does (see 6). In an
unmapped folder it starts the extension's bundled `claude.exe` (argv[0]) directly, without `CLAUDE_CONFIG_DIR`.

## 3. cswap's status lines corrupted `claude auth status --json`

**Symptom:** the extension log (`%APPDATA%\Code\logs\<ts>\window1\exthost\Anthropic.claude-code\Claude
VSCode.log`) repeated every few seconds:

```
claude auth status parse failed: SyntaxError: Unexpected non-whitespace character after JSON at position 460 (line 13 column 1)
```

**Cause:** claude-swap prints "CLAUDE_CONFIG_DIR is already set …", "Not sharing skills …" and "Launching
Account-1 … [session mode]" to **stdout**. Python holds stdout back when it's a pipe, so these lines come out when
cswap exits, which is right after Claude's JSON. Reproduced by running the wrapper with `auth status --json` and
capturing stdout: lines 1–12 were the JSON and lines 13–15 were cswap's messages.

**Fix (in this wrapper):** for `auth status` without `--text`, the wrapper captures stdout, writes only the first
complete JSON value to stdout, and sends the rest to stderr. The parse errors stopped after this was deployed.

**Upstream idea:** claude-swap could print its status lines to stderr, or flush them before launching Claude.

## 4. The VS Code history list ignores the wrapper's config folder

**Symptom:** in a mapped workspace the conversation list showed only history from `~/.claude/projects`.
Conversations created through the wrapper (saved in the session profile) were missing. Resuming an old conversation
from the list failed with "Couldn't load this conversation's saved history" whenever its file existed only in
`~/.claude`.

**Observed behavior:**

- With `claudeCode.claudeProcessWrapper` set, the extension builds its conversation list from its **own** config
  folder: the `CLAUDE_CONFIG_DIR` of the VS Code extension host if set, otherwise `~/.claude`. It does run
  `claude auth status --json` through the wrapper, but doesn't switch the list to the `configDirectory` that call
  reports. As far as we could tell, the reported folder only matters when checking whether a conversation can be
  resumed.
- `claudeCode.environmentVariables` and `claudeCode.claudeProcessWrapper` are **machine**-scoped settings (see the
  extension's `package.json`), so they can't differ per workspace.

So the wrapper can't redirect the list: the extension host process starts the wrapper, and a child process can't
change its parent's environment. The fix in 3 still matters (no parse errors), but it doesn't change the list.

**Options considered:**

| Option | Status |
|---|---|
| Per-workspace `CLAUDE_CONFIG_DIR` via VS Code settings | Not possible (machine-scoped settings). A workspace's `.vscode/settings.json` is often committed to its repo anyway. |
| direnv VS Code extension loading `.envrc` into the extension host | Not used here (reportedly unreliable on Windows). |
| Launch VS Code with `CLAUDE_CONFIG_DIR` set | Applies to the whole VS Code instance and only if it isn't already running. Fragile. |
| claude-swap `run --share-history` | "Not supported on Windows" (claude-swap help). |
| Junction `session profile\projects\<dir>` → `~/.claude\projects\<dir>` | **Rejected.** If claude-swap ever deletes the session profile (`remove`, `purge`), a recursive delete could follow the junction and wipe the real history (older Python `shutil.rmtree` recurses into Windows junctions). |
| Junction `~/.claude\projects\<dir>` → `session profile\projects\<dir>` | **Applied.** The real files stay in the session profile, where the CLI writes them. `~/.claude` only holds a pointer, so the list sees the same files. It's per project folder, including each worktree's own project folder. |

### One-time merge of existing history

When `~/.claude/projects/<project-folder>` already holds real history for a mapped project (from before it was
mapped), merge it into the session profile once, then replace it with the junction:

1. Copy everything from `~/.claude\projects\<project-folder>` into the session profile's
   `projects\<project-folder>` without overwriting newer files (`robocopy /E /XO`). Check `memory\MEMORY.md`: if both
   sides added entries, merge them by hand.
2. Move the `~/.claude` copy out of `projects\` to a backup folder.
3. Create the junction:
   ```powershell
   New-Item -ItemType Junction -Path "$HOME\.claude\projects\<project-folder>" -Target "$HOME\.claude-swap-backup\sessions\<n>-<email>\projects\<project-folder>"
   ```

### Automated in the wrapper

Two conversations went missing from the list the next day. They had switched into git worktrees, so Claude saved
them under the worktrees' own project folders (`<project-folder>--claude-worktrees-<name>`) in the session profile.
**The wrapper now does the linking itself** on every launch in a mapped folder. It creates the missing junctions for
the project's own and its worktrees' history folders and leaves existing ones alone (see the README's "History
list" section). A history folder that already exists for real in `~/.claude` still needs the one-time merge above.

**Undo:** remove only the junction (`cmd /c rmdir <link>`) and move the backup folder back into `projects\`.

**Don't** delete a junction with a recursive delete (`Remove-Item -Recurse`, `rm -rf`, Explorer on older Windows).
Those can follow it and delete the real files in the session profile.

### Chats that moved into a worktree

Linking worktree history folders wasn't enough. When a chat switches into a worktree, Claude moves its transcript
from `<project-folder>\` to `<project-folder>--claude-worktrees-<name>\`. It's a rename: the creation time is kept, and
the worktree's history folder is created at the moment of the switch. The project window's list only reads the
project's own history folder, not its worktrees' (no setting changes this in 2.1.287–2.1.289). So those chats drop
out of the list once they're closed. While open they still show, which is why they looked fine on the day they ran.

- **Bookmarks** are stored per conversation in VS Code's global storage for the extension
  (`%APPDATA%\Code\User\globalStorage\anthropic.claude-code\session-bookmarks\<id>.json`). A bookmarked chat whose
  transcript the panel can't find shows no bookmarks, but the files stay. They're hidden, not deleted.
- **Copies are the wrong fix.** Copies of two such chats went stale within days, because the chats continued in
  the worktree originals. Resuming a copy would fork the conversation.
- **Hard links are the right fix.** Claude appends to transcripts in place: a live transcript kept its NTFS file ID
  while growing, and the stale copies were exact byte prefixes of the originals. So a hard link stays one file under
  both names.

**Applied:** the wrapper hard-links worktree chats into the project's main history folder and junctions their details
folders (README, "History list"). The stale copies were checked to be exact prefixes of the originals, moved to a
backup folder, and replaced by links to the originals.

## 5. Where the history split came from, and why the wrapper forces session mode

Without the wrapper's placeholder, `cswap run` for a mapped folder picks `~/.claude` when the mapped account
happens to be the default login at launch, and the session profile otherwise. One workspace's conversations then
move between two folders depending on which account was the default at the time. That's the original reason for
step 3 in the README. Forcing session mode keeps each workspace's history in one place, at the cost of finding 1,
which is avoided by keeping the default login on an unmapped account.

## 6. claude-swap mapping behaviour (from `src/claude_swap/mappings.py`)

- Keys are `normcase(resolve(path))`, so on Windows they are stored lowercased (`d:\work\project`).
  Display tools should get the on-disk casing from the filesystem.
- A folder resolves to its **most specific** (longest) mapped ancestor-or-self.
- `map` upserts without checking that the path or account exists, and never prompts.
- `unmap` returns quietly even when nothing was mapped, so re-read `mappings.json` to confirm.
- `claude-swap map` has no `--json` output.

## 7. Other things worth knowing

- `claude-swap list --token-status` is the quickest health check. It shows each copy (active profile, session
  profile, stored backup) as `fresh`/`expired` with `refresh token yes/no`. It can't be combined with `--json`.
- `claude auth status --json` reports `email`, `orgId`, `configDirectory` and `projectsDirectory` for whatever
  `CLAUDE_CONFIG_DIR` is in effect. It's useful for checking which login a config folder holds.
- `claude auth login --email <addr>` signs in to the current config folder non-interactively apart from the browser
  step. That's what a scripted re-login uses.
- The extension skips its own update check when a process wrapper is configured ("skipped, a process wrapper is
  configured" in its log).
- The extension failed to spawn a `.cmd` wrapper (`spawn EINVAL`), which is why this is an exe.

## Possible upstream reports

- **claude-swap:** print status messages to stderr (or flush before launching). Don't renew a session profile's
  token at bootstrap while the same account is the live default login. Consider junction-safe deletes on Windows.
- **Claude Code VS Code extension:** when a process wrapper is configured, let the history list follow the
  `configDirectory` / `projectsDirectory` that `claude auth status` reports, and offer a way to list conversations
  that moved into a worktree.
