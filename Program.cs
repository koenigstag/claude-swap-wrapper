using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

// argv[0] from the VS Code extension is the path to its bundled claude.exe;
// the rest are the arguments meant for it.
var bundledClaude = args.Length > 0 ? args[0] : null;
var rest = args.Length > 1 ? args[1..] : Array.Empty<string>();
var cwd = Environment.CurrentDirectory;

var mapping = FindMapping(cwd);
var direct = mapping is null && bundledClaude is not null && File.Exists(bundledClaude);
var dryRun = Environment.GetEnvironmentVariable("CSWAP_WRAPPER_DRYRUN") == "1";
var linkOnly = Environment.GetEnvironmentVariable("CSWAP_WRAPPER_LINK_ONLY") == "1";

// CSWAP_WRAPPER_DRYRUN=1: print the decision (and planned history links) and
// exit, launching nothing and changing nothing.
if (dryRun)
{
    Console.WriteLine(direct
        ? $"direct: {bundledClaude} (cwd {cwd} is not mapped)"
        : $"cswap run (session mode): cwd {cwd} mapped via {mapping?.Key ?? "<none found, but no usable bundled claude>"}");
}

Log($"{(direct ? "direct" : "cswap ")} cwd={cwd} mapping={mapping?.Key ?? "-"} args={string.Join(' ', rest)}");

// Mapped folder: make this project's history (and its worktrees') visible to
// the extension's list, which always reads ~/.claude/projects. See LinkHistory.
if (mapping is not null)
{
    LinkHistory(cwd, mapping.Email, dryRun);
}

// CSWAP_WRAPPER_LINK_ONLY=1: just do the linking above (e.g. to catch up
// after a worktree session) without launching anything.
if (dryRun || linkOnly)
{
    return 0;
}

var psi = new ProcessStartInfo { UseShellExecute = false };

if (direct)
{
    // Unmapped folder: behave as if there were no wrapper — the extension's own
    // claude on the default login (~/.claude). Going through `cswap run` here
    // would launch plain claude with our placeholder CLAUDE_CONFIG_DIR still
    // set, and claude would treat it as a real (relative) config folder.
    psi.FileName = bundledClaude!;
    psi.Environment.Remove("CLAUDE_CONFIG_DIR");
}
else
{
    // Mapped folder. cswap can't be told to use a specific claude binary (it
    // resolves "claude" via PATH itself), so the bundled path is dropped and
    // the rest is forwarded to `cswap run -- ...`, which picks the account via
    // cwd -> mapping resolution.
    psi.FileName = "cswap";
    psi.ArgumentList.Add("run");
    psi.ArgumentList.Add("--");

    // cswap's "same-account fast path" (session.py: run()) launches straight
    // into the global ~/.claude when the mapped account happens to equal
    // whatever account is globally active AT THAT INSTANT, instead of the
    // stable per-account session profile. Since the globally active account
    // can change between spawns (other terminals, cswap auto, manual
    // switches), a single VS Code workspace's conversations would silently
    // jump between two different storage locations and old resume IDs 404.
    // Presetting CLAUDE_CONFIG_DIR makes cswap skip that fast path
    // unconditionally (session.py: "CLAUDE_CONFIG_DIR is already set ...
    // overriding it for this launch") and always use the persistent
    // per-account session dir instead -- the placeholder value itself is
    // never read, only its presence is checked before it gets overwritten.
    psi.Environment["CLAUDE_CONFIG_DIR"] = "unused-forces-session-mode";
}

foreach (var a in rest)
{
    psi.ArgumentList.Add(a);
}

// The extension runs `claude auth status --json` through us. Through cswap,
// cswap's own status lines ("Launching Account-1 ...") share stdout and —
// block-buffered when piped — land right after the JSON, which breaks the
// extension's JSON.parse. For this short one-shot call, capture stdout, emit
// only the JSON document, and move the rest to stderr. (The extension still
// lists history from ~/.claude regardless: with a process wrapper it ignores
// the reported configDirectory. LinkHistory covers that.)
var cleanJson = !direct && IsAuthStatusJson(rest);
psi.RedirectStandardOutput = cleanJson;

using var proc = Process.Start(psi);
if (cleanJson)
{
    var output = proc!.StandardOutput.ReadToEnd();
    proc.WaitForExit();
    var (json, other) = SplitJson(output);
    Console.Out.Write(json);
    Console.Out.Flush();
    if (other.Trim().Length > 0)
    {
        Console.Error.Write(other);
    }
    return proc.ExitCode;
}
proc!.WaitForExit();
return proc.ExitCode;

// One line per launch in %LOCALAPPDATA%\claude-swap-wrapper\wrapper.log (kept
// under ~1 MB). Arguments only: prompts travel over stdin and aren't logged.
static void Log(string line)
{
    try
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "claude-swap-wrapper");
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "wrapper.log");
        if (File.Exists(file) && new FileInfo(file).Length > 1_000_000)
        {
            File.Move(file, file + ".1", overwrite: true);
        }
        File.AppendAllText(file, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
    }
    catch (Exception)
    {
        // Logging must never break a launch.
    }
}

// `auth status` prints JSON unless --text is given.
static bool IsAuthStatusJson(string[] a) =>
    a.Length >= 2 && a[0] == "auth" && a[1] == "status" && !a.Contains("--text");

// The first complete JSON value in `text`, and everything else. When there's
// no parsable JSON, everything is returned unchanged as the "json" part.
static (string Json, string Other) SplitJson(string text)
{
    var start = text.IndexOf('{');
    if (start < 0)
    {
        return (text, "");
    }
    var bytes = System.Text.Encoding.UTF8.GetBytes(text[start..]);
    try
    {
        var reader = new Utf8JsonReader(bytes);
        if (!JsonDocument.TryParseValue(ref reader, out var doc))
        {
            return (text, "");
        }
        doc.Dispose();
        var jsonPart = System.Text.Encoding.UTF8.GetString(bytes, 0, (int)reader.BytesConsumed);
        var after = System.Text.Encoding.UTF8.GetString(bytes, (int)reader.BytesConsumed, bytes.Length - (int)reader.BytesConsumed);
        return (jsonPart + Environment.NewLine, text[..start] + after);
    }
    catch (JsonException)
    {
        return (text, "");
    }
}

// The extension lists history from ~/.claude/projects/<folder> (with a process
// wrapper it ignores the session profile reported by `auth status`), but a
// mapped launch saves it in the account's session profile. For each history
// folder of this project — its own and its worktrees'
// (<name>--claude-worktrees-*) — that exists in the session profile but not in
// ~/.claude/projects, create a junction there pointing at it. Never moves,
// merges or deletes anything; an existing folder or link is left alone.
static void LinkHistory(string cwd, string email, bool dryRun)
{
    try
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var sessions = Path.Combine(home, ".claude-swap-backup", "sessions");
        var profile = Directory.Exists(sessions)
            ? Directory.EnumerateDirectories(sessions)
                .FirstOrDefault(d => Path.GetFileName(d).EndsWith("-" + email.Replace('@', '_'), StringComparison.OrdinalIgnoreCase))
            : null;
        var profileProjects = profile is null ? null : Path.Combine(profile, "projects");
        if (profileProjects is null || !Directory.Exists(profileProjects))
        {
            return;
        }
        var globalProjects = Path.Combine(home, ".claude", "projects");
        Directory.CreateDirectory(globalProjects);

        var name = ProjectFolderName(cwd);
        var worktreePrefix = name + "--claude-worktrees-";
        foreach (var source in Directory.EnumerateDirectories(profileProjects))
        {
            var folder = Path.GetFileName(source);
            if (!folder.Equals(name, StringComparison.OrdinalIgnoreCase)
                && !folder.StartsWith(worktreePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // NTFS is case-insensitive, so this also finds d--… for D--….
            var link = Path.Combine(globalProjects, folder);
            if (Directory.Exists(link) || File.Exists(link))
            {
                continue;
            }
            if (dryRun)
            {
                Console.WriteLine($"would link: {link} -> {source}");
                continue;
            }
            MakeJunction(link, source, "history");
        }

        // The project window's conversation list only reads the project's own
        // history folder, not its worktrees', and a chat that switches into a
        // worktree has its transcript moved there.
        var main = Directory.EnumerateDirectories(profileProjects)
            .FirstOrDefault(d => Path.GetFileName(d).Equals(name, StringComparison.OrdinalIgnoreCase));
        if (main is not null)
        {
            LinkWorktreeChats(profileProjects, main, worktreePrefix, dryRun);
        }
    }
    catch (Exception e)
    {
        Log($"history linking skipped: {e.Message}");
    }
}

// Make chats that moved into one of the project's worktrees show in the
// project's own list: hard-link each worktree transcript into the main history
// folder (same file under a second name — Claude appends in place and moves by
// renaming, so both names stay one file), and junction its per-chat subfolder
// (subagents, tool results). Only adds; an existing name is left alone.
static void LinkWorktreeChats(string profileProjects, string main, string worktreePrefix, bool dryRun)
{
    foreach (var worktree in Directory.EnumerateDirectories(profileProjects)
                 .Where(d => Path.GetFileName(d).StartsWith(worktreePrefix, StringComparison.OrdinalIgnoreCase)))
    {
        foreach (var transcript in Directory.EnumerateFiles(worktree, "*.jsonl"))
        {
            var id = Path.GetFileNameWithoutExtension(transcript);
            if (!Guid.TryParse(id, out _))
            {
                continue;
            }

            var mainTranscript = Path.Combine(main, id + ".jsonl");
            if (File.Exists(mainTranscript) || Directory.Exists(mainTranscript))
            {
                // Already listed: an earlier link, or a chat that has a real
                // file here (e.g. a manual copy). Never overwrite.
            }
            else if (dryRun)
            {
                Console.WriteLine($"would hard-link chat: {mainTranscript} = {transcript}");
            }
            else if (Native.CreateHardLink(mainTranscript, transcript, IntPtr.Zero))
            {
                Log($"linked worktree chat: {mainTranscript} = {transcript}");
            }
            else
            {
                Log($"worktree chat link FAILED (win32 {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}): {mainTranscript} = {transcript}");
            }

            var details = Path.Combine(worktree, id);
            var mainDetails = Path.Combine(main, id);
            if (!Directory.Exists(details) || Directory.Exists(mainDetails) || File.Exists(mainDetails))
            {
                continue;
            }
            if (dryRun)
            {
                Console.WriteLine($"would link chat details: {mainDetails} -> {details}");
                continue;
            }
            MakeJunction(mainDetails, details, "chat details");
        }
    }
}

static void MakeJunction(string link, string target, string what)
{
    var mk = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    })!;
    mk.WaitForExit(10_000);
    Log(mk.ExitCode == 0
        ? $"linked {what}: {link} -> {target}"
        : $"{what} link FAILED ({mk.ExitCode}): {link} -> {target}: {mk.StandardError.ReadToEnd().Trim()}");
}

// Claude Code's project folder name: every non-alphanumeric character becomes
// '-' ("d:\work\app" -> "d--work-app").
static string ProjectFolderName(string dir) =>
    new string(Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar)
        .Select(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') ? c : '-').ToArray());

// The mapping that applies to `dir`, resolved like claude-swap's mappings.py:
// case-folded full paths, most specific (longest) match wins. Null when
// nothing is mapped or the file can't be read.
static Mapping? FindMapping(string dir)
{
    try
    {
        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude-swap-backup", "mappings.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        if (!doc.RootElement.TryGetProperty("mappings", out var mappings)
            || mappings.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var here = Normalize(dir);
        return mappings.EnumerateObject()
            .Select(m => new Mapping(
                Normalize(m.Name),
                m.Value.TryGetProperty("email", out var e) ? e.GetString() ?? "" : ""))
            .Where(m => here == m.Key || here.StartsWith(m.Key + Path.DirectorySeparatorChar))
            .OrderByDescending(m => m.Key.Length)
            .FirstOrDefault();
    }
    catch (Exception)
    {
        return null;
    }
}

static string Normalize(string path) =>
    Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar).ToLowerInvariant();

record Mapping(string Key, string Email);

static class Native
{
    // Hard link: a second name for the same file on the same volume.
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateHardLink(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes);
}
