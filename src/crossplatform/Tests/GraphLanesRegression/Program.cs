using System.Diagnostics;
using System.Text;
using GitExtensions.Avalonia.Services;

// Regression suite for the graph's relative / non-relative lanes: the "draw non-relatives
// gray" style and the lane highlight on ref-label hover (upstream 40e2457de), both computed
// by GraphRelativesPass.
//
// Usage:
//   dotnet run --project Tests/GraphLanesRegression/GraphLanesRegression.Harness.csproj
//   ... -- --measure <repo>      also times the passes on a real repository (read only)
//
// Exit code 0 means every case held; any other value means at least one did not.
//
// What it pins:
//   * the hover marks exactly the label's commits and their ancestors, also when the date
//     order lists a parent ABOVE its child (a ref on a commit with a skewed clock);
//   * the artificial rows are gray under the hover and coloured under the anchor style;
//   * a lane is coloured where a relative edge runs and nowhere else;
//   * computing lazily (row by row, as the screen asks) gives the same flags as computing
//     everything at once — the hover relies on it to stay proportional to the screen;
//   * the local branch <-> tracked remote pairing the hover groups by
//     (BranchTagRow.Upstream), for a remote upstream, a local one ("."), and none.

GitUI.CrossPlatformBootstrap.InitializeThreading();

List<string> failures = [];
int cases = 0;

const string Artificial = "1111111111111111111111111111111111111111";
static bool IsArtificial(RevisionRow row) => row.Hash == Artificial;

DateTime t0 = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

RevisionRow Row(string hash, int age, string[] parents, params string[] refs)
    => new(hash, hash, "a", t0.AddMinutes(-age), t0.AddMinutes(-age), hash, parents, refs);

// ------------------------------------------------------------------ a small DAG
//
//   W   (artificial, on top of main)
//   m   merge of a2 + b2          main
//   b2                            feature
//   a2
//   b1
//   x2  unrelated root line       other
//   a1
//   base
//   x1
List<RevisionRow> commits =
[
    Row("m", 0, ["a2", "b2"], "main"),
    Row("b2", 1, ["b1"], "feature"),
    Row("a2", 2, ["a1"]),
    Row("b1", 3, ["base"]),
    Row("x2", 4, ["x1"], "other"),
    Row("a1", 5, ["base"]),
    Row("base", 6, []),
    Row("x1", 7, []),
];
commits[0] = commits[0] with { IsHead = true };
List<RevisionRow> withArtificial = [Row(Artificial, -1, ["m"]), .. commits];
IReadOnlyList<RevisionRow> rows = RevisionService.BuildRevisionGraph(withArtificial);
RevisionParentIndex parentIndex = new(rows);
int IndexOf(string hash) => parentIndex.RowOf(hash);

// 1. Hover on "feature" (b2): b2, b1, base and nothing else.
{
    GraphRelativesPass pass = GraphRelativesPass.ForAncestorsOf(rows, parentIndex, [IndexOf("b2")], IsArtificial);
    pass.EnsureUpTo(rows.Count - 1);
    HashSet<string> expected = ["b2", "b1", "base"];
    string got = string.Join(",", rows.Where((r, i) => pass.Flags[i].Node).Select(r => r.Hash));
    Check("hover feature: exactly its commit and ancestors are relative",
        rows.Select((r, i) => pass.Flags[i].Node == expected.Contains(r.Hash)).All(b => b), got);

    // The artificial row is no ancestor of anything: gray under the hover.
    Check("hover feature: the working-directory row is gray", !pass.Flags[0].Node, got);

    // Every lane of the merge row is gray: m is no ancestor of b2, and no relative edge
    // has started yet above it.
    int mi = IndexOf("m");
    Check("hover feature: nothing above the label is coloured",
        pass.Flags.Take(IndexOf("b2")).All(f => !f.Node && f.Segments.All(s => !s)),
        Describe(pass, mi));

    // Between b2 and base an edge of b2's line crosses every boundary: each of those rows
    // hands at least one coloured segment down.
    bool crossing = true;
    for (int i = IndexOf("b2"); i < IndexOf("base"); i++)
    {
        RevisionRow row = rows[i];
        crossing &= row.GraphSegments.Where((s, k) => pass.Flags[i].Segments[k] && s.ToY >= 1.0).Any();
    }

    Check("hover feature: a coloured lane runs from the label down to base", crossing, Describe(pass, IndexOf("a2")));

    // The unrelated root line stays gray all the way.
    Check("hover feature: the unrelated line is gray",
        !pass.Flags[IndexOf("x2")].Node && !pass.Flags[IndexOf("x1")].Node, got);

    // And the a-line nodes too (a2, a1 are not ancestors of b2).
    Check("hover feature: the sibling line is gray",
        !pass.Flags[IndexOf("a2")].Node && !pass.Flags[IndexOf("a1")].Node, got);
}

// 2. A group (local + its tracked remote) is the union of both histories.
{
    GraphRelativesPass pass = GraphRelativesPass.ForAncestorsOf(rows, parentIndex, [IndexOf("b2"), IndexOf("a2")], IsArtificial);
    pass.EnsureUpTo(rows.Count - 1);
    HashSet<string> expected = ["b2", "b1", "a2", "a1", "base"];
    Check("group hover: union of both ancestries",
        rows.Select((r, i) => pass.Flags[i].Node == expected.Contains(r.Hash)).All(b => b),
        string.Join(",", rows.Where((r, i) => pass.Flags[i].Node).Select(r => r.Hash)));
}

// 3. Lazy equals eager, for both kinds of pass.
{
    foreach ((string name, Func<GraphRelativesPass> make) in new (string, Func<GraphRelativesPass>)[]
    {
        ("hover", () => GraphRelativesPass.ForAncestorsOf(rows, parentIndex, [IndexOf("b2")], IsArtificial)),
        ("anchor", () => GraphRelativesPass.ForSet(rows, ["m", "a2", "b2", "a1", "b1", "base"], IsArtificial)),
    })
    {
        GraphRelativesPass eager = make();
        eager.EnsureUpTo(rows.Count - 1);
        GraphRelativesPass lazy = make();
        bool same = true;
        foreach (int i in new[] { 3, 1, 2, 7, 5, 8, 0, 4, 6 })
        {
            (bool Node, bool[] Segments)? f = lazy.At(i);
            same &= f is { } v && v.Node == eager.Flags[i].Node && v.Segments.SequenceEqual(eager.Flags[i].Segments);
        }

        Check($"{name}: lazy flags equal eager flags", same, $"computed {lazy.Flags.Count}");
        Check($"{name}: At() stops at the row asked for", make().At(2) is not null && LazyCount(make, 2) == 3, "");
        Check($"{name}: At() out of range is null", make().At(rows.Count) is null && make().At(-1) is null, "");
    }
}

// 4. The anchor style is unchanged: the artificial row is coloured, an empty set means all.
{
    GraphRelativesPass anchor = GraphRelativesPass.ForSet(rows, ["b2", "b1", "base"], IsArtificial);
    anchor.EnsureUpTo(rows.Count - 1);
    Check("anchor: the working-directory row stays coloured", anchor.Flags[0].Node, "");

    GraphRelativesPass all = GraphRelativesPass.ForSet(rows, [], IsArtificial);
    all.EnsureUpTo(rows.Count - 1);
    Check("anchor: an empty set colours everything",
        all.Flags.All(f => f.Node && f.Segments.All(s => s)), "");
}

// 5. A parent listed ABOVE its child (date order with a skewed clock): still an ancestor.
{
    List<RevisionRow> skewed =
    [
        Row("p", 0, ["base2"], "tip-on-parent"),
        Row("c", 1, ["p"], "label"),
        Row("base2", 2, []),
    ];
    IReadOnlyList<RevisionRow> graphed = RevisionService.BuildRevisionGraph(skewed);
    RevisionParentIndex skewedIndex = new(graphed);
    GraphRelativesPass pass = GraphRelativesPass.ForAncestorsOf(
        graphed, skewedIndex, [skewedIndex.RowOf("c")], IsArtificial);
    pass.EnsureUpTo(graphed.Count - 1);
    Check("skewed order: a parent above its child is still relative",
        pass.Flags.All(f => f.Node), string.Join(",", pass.Flags.Select(f => f.Node)));
}

// 6. BranchTagRow.Upstream, from a real repository.
{
    string repo = Path.Combine(Path.GetTempPath(), "gea-graphlanes-" + Environment.ProcessId);
    if (Directory.Exists(repo))
    {
        Directory.Delete(repo, recursive: true);
    }

    Directory.CreateDirectory(repo);
    try
    {
        Git(repo, "init -q -b main");
        Git(repo, "config user.name t");
        Git(repo, "config user.email t@t");
        File.WriteAllText(Path.Combine(repo, "f"), "1");
        Git(repo, "add f");
        Git(repo, "commit -q -m one");
        Git(repo, "remote add origin https://example.invalid/r.git");
        Git(repo, "update-ref refs/remotes/origin/main HEAD");
        Git(repo, "config branch.main.remote origin");
        Git(repo, "config branch.main.merge refs/heads/main");
        Git(repo, "branch dev");
        Git(repo, "config branch.dev.remote .");
        Git(repo, "config branch.dev.merge refs/heads/main");
        Git(repo, "branch lone");

        BranchTagListing listing = new BranchTagService().LoadRefs(repo);
        string UpstreamOf(string name) => listing.Branches.FirstOrDefault(b => b.Name == name)?.Upstream ?? "<missing>";

        Check("upstream: main tracks origin/main", UpstreamOf("main") == "origin/main", UpstreamOf("main"));
        Check("upstream: dev tracks the local main", UpstreamOf("dev") == "main", UpstreamOf("dev"));
        Check("upstream: lone tracks nothing", UpstreamOf("lone") == string.Empty, UpstreamOf("lone"));
        Check("upstream: a remote branch has none", UpstreamOf("origin/main") == string.Empty, UpstreamOf("origin/main"));
    }
    finally
    {
        Directory.Delete(repo, recursive: true);
    }
}

// ------------------------------------------------------------------ optional measurement
int measure = Array.IndexOf(args, "--measure");
if (measure >= 0 && measure + 1 < args.Length)
{
    Measure(args[measure + 1]);
}

if (failures.Count > 0)
{
    foreach (string failure in failures)
    {
        Console.WriteLine($"FAIL: {failure}");
    }

    Console.WriteLine($"FAILED: {failures.Count} of {cases} graph-lane cases");
    return 1;
}

Console.WriteLine($"PASS: {cases} graph-lane cases (hover ancestry, groups, lazy pass, upstream pairing)");
return 0;

int LazyCount(Func<GraphRelativesPass> make, int index)
{
    GraphRelativesPass pass = make();
    pass.At(index);
    return pass.Flags.Count;
}

string Describe(GraphRelativesPass pass, int index)
    => index < 0 || index >= pass.Flags.Count
        ? "<none>"
        : $"{rows[index].Hash}: node={pass.Flags[index].Node} segs=[{string.Join(",", pass.Flags[index].Segments)}]";

void Check(string what, bool held, string detail)
{
    cases++;
    if (!held)
    {
        failures.Add($"{what} — got: {detail}");
    }
}

static void Git(string repo, string arguments)
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
    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"git {arguments}: {output}");
    }
}

// Times what a hover costs on a real history against what every rebind already paid for
// the anchor style: the full ancestor walk, the first screen of rows, and (worst case) the
// whole list, which is what a hover at the very bottom of a fully loaded grid computes.
static void Measure(string repo)
{
    Stopwatch load = Stopwatch.StartNew();
    IReadOnlyList<RevisionRow> rows = new RevisionService().LoadRevisions(repo, 1_000_000);
    load.Stop();
    Console.WriteLine($"measure: {rows.Count} rows loaded in {load.ElapsedMilliseconds} ms");

    static bool NotArtificial(RevisionRow _) => false;

    double Time(Action action, int rounds = 20)
    {
        action();
        Stopwatch sw = Stopwatch.StartNew();
        for (int i = 0; i < rounds; i++)
        {
            action();
        }

        return sw.Elapsed.TotalMilliseconds / rounds;
    }

    RevisionParentIndex parentIndex = new(rows);
    double index = Time(() => parentIndex = new RevisionParentIndex(rows));

    HashSet<string> all = [.. rows.Select(r => r.Hash)];
    double anchorFull = Time(() => GraphRelativesPass.ForSet(rows, all, NotArtificial).EnsureUpTo(rows.Count - 1));

    int mid = rows.Count / 2;
    double hoverTop = Time(() => GraphRelativesPass.ForAncestorsOf(rows, parentIndex, [0], NotArtificial).At(60));
    double hoverMid = Time(() => GraphRelativesPass.ForAncestorsOf(rows, parentIndex, [mid], NotArtificial).At(mid + 60));
    double hoverBottom = Time(() => GraphRelativesPass.ForAncestorsOf(rows, parentIndex, [0], NotArtificial).EnsureUpTo(rows.Count - 1));

    Console.WriteLine($"measure: parent index (once per row set)       {index,8:F2} ms");
    Console.WriteLine($"measure: anchor style, whole list (each rebind) {anchorFull,8:F2} ms");
    Console.WriteLine($"measure: hover at the top, first screen         {hoverTop,8:F2} ms");
    Console.WriteLine($"measure: hover mid-list, screen at mid          {hoverMid,8:F2} ms");
    Console.WriteLine($"measure: hover, whole list (worst case)         {hoverBottom,8:F2} ms");
}
