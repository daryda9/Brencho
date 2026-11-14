using System.Diagnostics;
using System.Text;
using GitCommands;
using GitExtensions.Avalonia.Services;
using GitExtensions.Extensibility.Git;

// Regression suite for what the revision diff can now WRITE: per-line cherry-pick and
// revert of a committed patch (upstream FileViewer.ApplySelectedLines, with fd1a1b0e7
// for copied files), "Reset file(s) to selected/parent" (FileStatusList, with
// 52d08e996), and the AVIF sniff + system decode (e2a7dc3ab).
//
// Usage: dotnet run --project Tests/RevisionPatchRegression/RevisionPatchRegression.Harness.csproj
//
// Exit code 0 means every case held. Everything runs against real repositories in a
// scratch directory: the patch text comes from the same DiffTextService the pane
// renders, and the result is read back from the work tree and the index, because what
// matters is what git did, not what the port believes it asked for.

GitUI.CrossPlatformBootstrap.InitializeThreading();

List<string> failures = [];
int cases = 0;

string scratch = Path.Combine(
    Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(),
    "ge-revision-patch-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);

try
{
    await RunModifiedApplyAndRevertAsync();
    await RunAddedApplyAndRevertAsync();
    await RunCopiedApplyAsync();
    await RunRenamedRevertAsync();
    RunRetargetHeader();
    RunHasFilesWhichMayBeDeleted();
    RunResetToParentAndSelected();
    RunAvif();
}
finally
{
    try
    {
        Directory.Delete(scratch, recursive: true);
    }
    catch (Exception)
    {
        // run-all.sh keeps its own scratch on failure.
    }
}

if (failures.Count > 0)
{
    foreach (string failure in failures)
    {
        Console.WriteLine($"FAIL: {failure}");
    }

    Console.WriteLine($"FAILED: {failures.Count} of {cases} revision-patch cases");
    return 1;
}

Console.WriteLine($"PASS: {cases} revision-patch cases (line apply/revert, reset to revision, AVIF) against real repositories");
return 0;

// ---------------------------------------------------------------- the cases

// Cherry-pick two of a commit's lines onto a work tree that does not have the commit,
// then revert one line of a commit that IS checked out.
async Task RunModifiedApplyAndRevertAsync()
{
    string repo = Init("modified");
    Write(repo, "f.txt", Lines("a", "b", "c", "d", "e", "f", "g", "h", "i", "j"));
    string baseCommit = Commit(repo, "base");
    Write(repo, "f.txt", Lines("a", "b", "C", "d", "e", "f", "g", "H", "i", "j"));
    string change = Commit(repo, "change");

    Git(repo, $"reset -q --hard {baseCommit}");
    string diff = await CommitDiffAsync(repo, change, "f.txt");
    (int start, int length) = Range(diff, "-c", "+C");
    PatchStagingResult applied = PatchStagingService.Apply(
        repo, diff, start, length, PatchStagingAction.ApplyToWorkTree, isNewFile: false, isRenamed: false);
    Check("apply: git apply succeeds", applied.Success, applied.Output);
    Check("apply: the selected change reached the work tree",
        Read(repo, "f.txt") == Lines("a", "b", "C", "d", "e", "f", "g", "h", "i", "j"), Read(repo, "f.txt"));
    Check("apply: and the index (--index)",
        Git(repo, "show :f.txt") == Lines("a", "b", "C", "d", "e", "f", "g", "h", "i", "j"), Git(repo, "show :f.txt"));

    Git(repo, $"reset -q --hard {change}");
    (start, length) = Range(diff, "-h", "+H");
    PatchStagingResult reverted = PatchStagingService.Apply(
        repo, diff, start, length, PatchStagingAction.RevertInWorkTree, isNewFile: false, isRenamed: false);
    Check("revert: git apply succeeds", reverted.Success, reverted.Output);
    Check("revert: only the selected change is undone",
        Read(repo, "f.txt") == Lines("a", "b", "C", "d", "e", "f", "g", "h", "i", "j"), Read(repo, "f.txt"));

    // A work tree that moved on since the commit: --3way still lands the line.
    Git(repo, $"reset -q --hard {baseCommit}");
    Write(repo, "f.txt", Lines("a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k"));
    Git(repo, "commit -qam later");
    (start, length) = Range(diff, "-h", "+H");
    PatchStagingResult moved = PatchStagingService.Apply(
        repo, diff, start, length, PatchStagingAction.ApplyToWorkTree, isNewFile: false, isRenamed: false);
    Check("apply on a moved work tree succeeds", moved.Success, moved.Output);
    Check("apply on a moved work tree keeps the later edit",
        Read(repo, "f.txt") == Lines("a", "b", "c", "d", "e", "f", "g", "H", "i", "j", "k"), Read(repo, "f.txt"));
}

// An added file: cherry-picking some of its lines CREATES it with just those lines;
// reverting some of them edits the existing file (the header rewrite).
async Task RunAddedApplyAndRevertAsync()
{
    string repo = Init("added");
    Write(repo, "keep.txt", "keep\n");
    string baseCommit = Commit(repo, "base");
    Write(repo, "n.txt", Lines("x", "y", "z"));
    string added = Commit(repo, "add n");

    string diff = await CommitDiffAsync(repo, added, "n.txt");
    Check("added: the patch is a new-file diff", diff.Contains("--- /dev/null", StringComparison.Ordinal), diff);

    (int start, int length) = Range(diff, "+x", "+y");
    PatchStagingResult reverted = PatchStagingService.Apply(
        repo, diff, start, length, PatchStagingAction.RevertInWorkTree, isNewFile: true, isRenamed: false);
    Check("added revert: git apply succeeds", reverted.Success, reverted.Output);
    Check("added revert: the selected lines are gone", Read(repo, "n.txt") == Lines("z"), Read(repo, "n.txt"));

    Git(repo, $"reset -q --hard {baseCommit}");
    (start, length) = Range(diff, "+y");
    PatchStagingResult applied = PatchStagingService.Apply(
        repo, diff, start, length, PatchStagingAction.ApplyToWorkTree, isNewFile: true, isRenamed: false);
    Check("added apply: git apply succeeds", applied.Success, applied.Output);
    Check("added apply: the file is created with the selected line only",
        File.Exists(Path.Combine(repo, "n.txt")) && Read(repo, "n.txt") == Lines("y"),
        File.Exists(Path.Combine(repo, "n.txt")) ? Read(repo, "n.txt") : "missing");
    Check("added apply: and it is in the index", Git(repo, "ls-files n.txt").Trim() == "n.txt", Git(repo, "ls-files"));
}

// fd1a1b0e7: a copy is listed as Copied (the core passes --find-copies, which only
// reports a copy whose source the same commit also edits), and its lines can be
// cherry-picked like an added file's.
async Task RunCopiedApplyAsync()
{
    string repo = Init("copied");
    string body = string.Concat(Enumerable.Range(1, 20).Select(i => $"line {i}\n"));
    Write(repo, "src.txt", body);
    string baseCommit = Commit(repo, "base");
    Write(repo, "dst.txt", body);
    Write(repo, "src.txt", body + "line 21\n");
    string copy = Commit(repo, "copy");

    DiffFileRow? row = DiffService.GetChangedFiles(repo, copy).FirstOrDefault(r => r.Name == "dst.txt");
    Check("copied: the list reports the copy", row?.Kind == DiffChangeKind.Copied, row?.Kind.ToString() ?? "no row");

    // The premise of dropping the source from the pathspec in DiffView: with it, the
    // pane got TWO files (the copy as new, the edited source) and no patch could be cut.
    string withSource = await CommitDiffAsync(repo, copy, "dst.txt", "src.txt");
    Check("copied: naming the source too yields two diffs",
        CountOf(withSource, "diff --git ") == 2, withSource);

    string diff = await CommitDiffAsync(repo, copy, "dst.txt");
    Check("copied: its own path alone yields one new-file diff",
        CountOf(diff, "diff --git ") == 1 && diff.Contains("--- /dev/null", StringComparison.Ordinal), diff);

    Git(repo, $"reset -q --hard {baseCommit}");
    (int start, int length) = Range(diff, "+line 3");
    PatchStagingResult applied = PatchStagingService.Apply(
        repo, diff, start, length, PatchStagingAction.ApplyToWorkTree, isNewFile: true, isRenamed: false);
    Check("copied apply: git apply succeeds", applied.Success, applied.Output);
    Check("copied apply: the copy is created with the selected line",
        File.Exists(Path.Combine(repo, "dst.txt")) && Read(repo, "dst.txt") == "line 3\n",
        File.Exists(Path.Combine(repo, "dst.txt")) ? Read(repo, "dst.txt") : "missing");
}

// A rename with an edit: reverting the edit must patch the NEW name, not try to move
// the file back.
async Task RunRenamedRevertAsync()
{
    string repo = Init("renamed");
    string body = string.Concat(Enumerable.Range(1, 20).Select(i => $"row {i}\n"));
    Write(repo, "old.txt", body);
    Commit(repo, "base");
    Git(repo, "mv old.txt new.txt");
    Write(repo, "new.txt", body.Replace("row 10\n", "ROW 10\n", StringComparison.Ordinal));
    string rename = Commit(repo, "rename");

    string diff = await CommitDiffAsync(repo, rename, "new.txt", "old.txt");
    Check("renamed: the patch carries the rename", diff.Contains("rename from old.txt", StringComparison.Ordinal), diff);

    (int start, int length) = Range(diff, "-row 10", "+ROW 10");
    PatchStagingResult reverted = PatchStagingService.Apply(
        repo, diff, start, length, PatchStagingAction.RevertInWorkTree, isNewFile: false, isRenamed: true);
    Check("renamed revert: git apply succeeds", reverted.Success, reverted.Output);
    Check("renamed revert: the edit is undone under the new name",
        Read(repo, "new.txt") == body && !File.Exists(Path.Combine(repo, "old.txt")),
        Read(repo, "new.txt"));
}

void RunRetargetHeader()
{
    const string diff = "diff --git a/n.txt b/n.txt\nnew file mode 100644\nindex 0000000..1234567\n--- /dev/null\n+++ b/n.txt\n@@ -0,0 +1,2 @@\n+x\n+y\n";
    int original = diff.IndexOf("+y", StringComparison.Ordinal);
    (string text, int shifted) = PatchStagingService.RetargetCommittedPatch(diff, original, revert: true, isNewFile: true, isRenamed: false);
    Check("retarget: new file mode dropped", !text.Contains("new file mode", StringComparison.Ordinal), text);
    Check("retarget: old side is the file", text.Contains("--- a/n.txt\n", StringComparison.Ordinal), text);
    Check("retarget: index ids swapped", text.Contains("index 1234567..0000000\n", StringComparison.Ordinal), text);
    Check("retarget: selection follows the header", text.Substring(shifted, 2) == "+y", text.Substring(shifted, 2));

    (string forward, int same) = PatchStagingService.RetargetCommittedPatch(diff, original, revert: false, isNewFile: true, isRenamed: false);
    Check("retarget: forward keeps the new-file header", forward == diff && same == original, forward);
}

// 52d08e996, the four upstream tests (FileStatusListTests) plus the copy narrowing.
void RunHasFilesWhichMayBeDeleted()
{
    string dir = Path.Combine(scratch, "may-be-deleted");
    Directory.CreateDirectory(dir);
    FullPathResolver resolver = new(() => dir);

    GitItemStatus deleted = new("deleted.txt") { IsDeleted = true, IsTracked = true };
    Check("may-delete: a file deleted in the revision offers nothing",
        !RevisionResetService.HasFilesWhichMayBeDeleted([deleted], resolver), "true");

    GitItemStatus changed = new("changed.txt") { IsChanged = true, IsTracked = true };
    File.WriteAllText(Path.Combine(dir, "changed.txt"), "about to be reset");
    Check("may-delete: a changed file offers nothing",
        !RevisionResetService.HasFilesWhichMayBeDeleted([changed], resolver), "true");

    GitItemStatus copied = RevisionResetService.ToItemStatus(
        new DiffFileRow("copy.txt", "changed.txt", DiffChangeKind.Copied, true));
    Check("may-delete: a copy whose target is absent offers nothing (its source is never deleted)",
        !RevisionResetService.HasFilesWhichMayBeDeleted([copied.InvertStatus()], resolver), "true");

    File.WriteAllText(Path.Combine(dir, "deleted.txt"), "an untracked file at the same path");
    Check("may-delete: an existing path at a deleted item's name is offered",
        RevisionResetService.HasFilesWhichMayBeDeleted([deleted], resolver), "false");

    Directory.CreateDirectory(Path.Combine(dir, "submodule"));
    GitItemStatus submodule = new("submodule") { IsNew = true, IsSubmodule = true };
    Check("may-delete: an existing directory is offered",
        RevisionResetService.HasFilesWhichMayBeDeleted([submodule], resolver), "false");

    GitItemStatus renamed = new("gone.txt") { OldName = "changed.txt", IsRenamed = true, IsTracked = true };
    Check("may-delete: a rename is checked under its old name too",
        RevisionResetService.HasFilesWhichMayBeDeleted([renamed], resolver), "false");
}

void RunResetToParentAndSelected()
{
    string repo = Init("reset");
    Write(repo, "f.txt", "parent\n");
    Write(repo, "d.txt", "doomed\n");
    string parent = Commit(repo, "parent");
    Write(repo, "f.txt", "selected\n");
    Write(repo, "g.txt", "added\n");
    File.Delete(Path.Combine(repo, "d.txt"));
    string selected = Commit(repo, "selected");

    DiffFileRow[] rows = [.. DiffService.GetChangedFiles(repo, selected)];
    GitItemStatus[] items = [.. rows.Select(RevisionResetService.ToItemStatus)];
    Check("reset: the commit lists three changes", rows.Length == 3, string.Join(", ", rows.Select(r => r.Name)));

    string output = RevisionResetService.Reset(repo, parent, items, toParent: true, resetAndDelete: true);
    Check("reset to parent: no complaint", output.Length == 0, output);
    Check("reset to parent: the modified file is the parent's", Read(repo, "f.txt") == "parent\n", Read(repo, "f.txt"));
    Check("reset to parent: the added file is deleted", !File.Exists(Path.Combine(repo, "g.txt")), "still there");
    Check("reset to parent: the deleted file is back", File.Exists(Path.Combine(repo, "d.txt")), "missing");

    output = RevisionResetService.Reset(repo, selected, items, toParent: false, resetAndDelete: true);
    Check("reset to selected: no complaint", output.Length == 0, output);
    Check("reset to selected: the modified file is the commit's", Read(repo, "f.txt") == "selected\n", Read(repo, "f.txt"));
    Check("reset to selected: the added file is back", File.Exists(Path.Combine(repo, "g.txt")), "missing");
    Check("reset to selected: the deleted file is deleted again", !File.Exists(Path.Combine(repo, "d.txt")), "still there");
    Check("reset to selected: nothing is left to commit", Git(repo, "status --porcelain").Trim().Length == 0, Git(repo, "status --porcelain"));

    Check("reset: the zero id and the artificial ids are not resettable",
        !RevisionResetService.CanResetTo(new string('0', 40))
            && !RevisionResetService.CanResetTo(DiffService.WorkTreeHash)
            && !RevisionResetService.CanResetTo(DiffService.IndexHash)
            && RevisionResetService.CanResetTo(selected),
        "wrong answer");
}

void RunAvif()
{
    // 8×6 red, encoded by ffmpeg/libaom (major brand "avif").
    byte[] avif = Convert.FromBase64String(
        "AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUEAAAD5bWV0YQAAAAAAAAAvaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAFBpY3R1cmVIYW5kbGVyAAAAAA5waXRtAAAAAAABAAAAHmlsb2MAAAAARAAAAQABAAAAAQAAASEAAAAgAAAAKGlpbmYAAAAAAAEAAAAaaW5mZQIAAAAAAQAAYXYwMUNvbG9yAAAAAGppcHJwAAAAS2lwY28AAAAUaXNwZQAAAAAAAAAIAAAABgAAABBwaXhpAAAAAAMICAgAAAAMYXYxQ4EgAAAAAAATY29scm5jbHgAAQACAACAAAAAF2lwbWEAAAAAAAAAAQABBAECgwQAAAAobWRhdAoIOAi9bQENACAyFBgAAABQAAAABfXvZN1zAeOaG2HY");
    ReadOnlySpan<byte> head = avif.AsSpan(0, ImageFormats.HeaderLength);
    Check("avif: the ftyp signature is recognised", ImageFormats.IsAvif(head), "false");

    // HEIC shares the container but not the codec: it is not AVIF.
    byte[] heic = [.. avif];
    "heic"u8.CopyTo(heic.AsSpan(8));
    "heic"u8.CopyTo(heic.AsSpan(16));
    "heic"u8.CopyTo(heic.AsSpan(20));
    Check("avif: a HEIC header is not AVIF", !ImageFormats.IsAvif(heic.AsSpan(0, ImageFormats.HeaderLength)), "true");

    // Either answer is legitimate on a given machine; what must hold is that the two
    // agree, so that nothing is offered which the window would then fail to show.
    bool canDecode = SystemImageDecoder.CanDecodeAvif;
    Check("avif: named an image exactly when the system can decode it",
        (ImageFormats.Detect(head) == "AVIF") == canDecode, $"detect={ImageFormats.Detect(head)} decode={canDecode}");

    DecodedImage? image = SystemImageDecoder.TryDecodeAvif(avif);
    if (canDecode)
    {
        Check("avif: decoded at its size", image is { Width: 8, Height: 6 }, image is null ? "null" : $"{image.Width}x{image.Height}");
        Check("avif: decoded red", image is not null && image.Rgba[0] > 200 && image.Rgba[1] < 60 && image.Rgba[2] < 60 && image.Rgba[3] == 255,
            image is null ? "null" : $"{image.Rgba[0]},{image.Rgba[1]},{image.Rgba[2]},{image.Rgba[3]}");
        Check("avif: garbage is refused, not thrown", SystemImageDecoder.TryDecodeAvif([.. avif.Take(60)]) is null, "decoded");
    }
    else
    {
        Check("avif: without a decoder nothing is decoded", image is null, "decoded");
        Console.WriteLine("note: no gdk-pixbuf AVIF loader on this machine, decode cases skipped");
    }
}

// ---------------------------------------------------------------- helpers

Task<string> CommitDiffAsync(string repo, string commit, string path, string? oldPath = null)
    => DiffTextService.GetDiffTextAsync(
        new DiffTextRequest(DiffTextKind.Commit, repo, commit, null, path, oldPath),
        new DiffDisplayOptions());

// The character range from the start of the first named line to the end of the last,
// which is what DiffView.SelectedPatchRange hands the service.
static (int Start, int Length) Range(string diff, string first, string? last = null)
{
    int start = diff.IndexOf("\n" + first + "\n", StringComparison.Ordinal) + 1;
    string end = last ?? first;
    int endAt = diff.IndexOf("\n" + end + "\n", start - 1, StringComparison.Ordinal) + 1 + end.Length;
    return (start, endAt - start);
}

static string Lines(params string[] lines) => string.Concat(lines.Select(l => l + "\n"));

static int CountOf(string text, string what)
{
    int count = 0;
    for (int at = text.IndexOf(what, StringComparison.Ordinal); at >= 0; at = text.IndexOf(what, at + 1, StringComparison.Ordinal))
    {
        count++;
    }

    return count;
}

string Init(string name)
{
    string repo = Path.Combine(scratch, name);
    Directory.CreateDirectory(repo);
    Git(repo, "init -q -b main");
    Git(repo, "config user.name Harness");
    Git(repo, "config user.email harness@example.invalid");
    Git(repo, "config commit.gpgsign false");
    Git(repo, "config core.autocrlf false");
    return repo;
}

string Commit(string repo, string message)
{
    Git(repo, "add -A");
    Git(repo, $"commit -q -m \"{message}\"");
    return Git(repo, "rev-parse HEAD").Trim();
}

static void Write(string repo, string name, string text)
    => File.WriteAllText(Path.Combine(repo, name), text, new UTF8Encoding(false));

static string Read(string repo, string name) => File.ReadAllText(Path.Combine(repo, name));

string Git(string repo, string arguments)
{
    ProcessStartInfo info = new("git", arguments)
    {
        WorkingDirectory = repo,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false,
    };

    using Process process = Process.Start(info)!;
    StringBuilder output = new();
    output.Append(process.StandardOutput.ReadToEnd());
    output.Append(process.StandardError.ReadToEnd());
    process.WaitForExit();
    return output.ToString();
}

void Check(string what, bool held, string detail)
{
    cases++;
    if (!held)
    {
        failures.Add($"{what} — got: {detail}");
    }
}
