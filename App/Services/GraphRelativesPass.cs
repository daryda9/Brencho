namespace GitExtensions.Avalonia.Services;

/// <summary>
///  The per-row "is relative" flags the graph cells are drawn with (relative lanes keep
///  their colour, the others are gray): the node's own flag plus one flag per segment, in
///  the order the segment list comes out of <see cref="RevisionService.BuildRevisionGraph"/>.
///  Computed top-down and only as far as somebody asks (<see cref="EnsureUpTo"/>), so a pass
///  can stop at the last row on screen and resume when the user scrolls.
///
///  <para>Which NODES are relative:</para>
///  <list type="bullet">
///   <item><see cref="ForSet"/>: the "draw non-relatives gray" style — a set of hashes (the
///    highlight anchor and its ancestors); an empty set means "everything", and the
///    artificial rows (working directory / commit index) always are.</item>
///   <item><see cref="ForAncestorsOf"/>: the hovered ref label (upstream 40e2457de,
///    HoverHighlightCalculator) — the given rows and every ancestor of them. The artificial
///    rows are nobody's ancestor and stay gray, as upstream's HighlightedIds never
///    contains them.</item>
///  </list>
///
///  <para>The SEGMENT flags follow the node flags down the lanes: upstream a segment asks
///  its child (the newer end) for its flag; here the geometry only carries lanes, so the
///  flag is carried instead, which is equivalent — a bottom half leaving the node takes the
///  node's flag, every other half continues an edge opened further up and takes the flag
///  that edge arrived with, keyed by <see cref="RevisionGraphSegment.ColorLane"/> (the
///  edge identity) and never by the column, which BuildGraph recycles between unrelated
///  branches.</para>
/// </summary>
public sealed class GraphRelativesPass
{
    private readonly IReadOnlyList<RevisionRow> _rows;
    private readonly HashSet<string>? _set;
    private readonly bool[]? _ancestors;
    private readonly Func<RevisionRow, bool> _isArtificial;
    private HashSet<int> _incoming = [];
    private HashSet<int> _outgoing = [];

    private GraphRelativesPass(
        IReadOnlyList<RevisionRow> rows, HashSet<string>? set, bool[]? ancestors, Func<RevisionRow, bool> isArtificial)
    {
        _rows = rows;
        _set = set;
        _ancestors = ancestors;
        _isArtificial = isArtificial;
    }

    /// <summary>The flags computed so far, one entry per display row from the top.</summary>
    public List<(bool Node, bool[] Segments)> Flags { get; } = [];

    public static GraphRelativesPass ForSet(
        IReadOnlyList<RevisionRow> rows, HashSet<string> relatives, Func<RevisionRow, bool> isArtificial)
        => new(rows, relatives, ancestors: null, isArtificial);

    /// <summary>
    ///  The pass for a hovered label: the rows at <paramref name="seedRows"/> and all their
    ///  ancestors. The ancestor walk is complete rather than folded into the top-down pass,
    ///  because the default walk order is by date and does not promise that a parent is
    ///  listed after every child (a ref on a commit with a skewed clock comes out early) —
    ///  the same full walk upstream's WalkAncestors does. On row indices and a bitmap, so it
    ///  costs a fraction of a millisecond even on a long loaded history.
    /// </summary>
    public static GraphRelativesPass ForAncestorsOf(
        IReadOnlyList<RevisionRow> rows,
        RevisionParentIndex index,
        IEnumerable<int> seedRows,
        Func<RevisionRow, bool> isArtificial)
    {
        bool[] ancestors = new bool[rows.Count];
        Stack<int> stack = new();
        foreach (int seed in seedRows)
        {
            if (seed >= 0 && seed < ancestors.Length && !ancestors[seed])
            {
                ancestors[seed] = true;
                stack.Push(seed);
            }
        }

        while (stack.Count > 0)
        {
            foreach (int parent in index.ParentsOf(stack.Pop()))
            {
                if (!ancestors[parent])
                {
                    ancestors[parent] = true;
                    stack.Push(parent);
                }
            }
        }

        return new(rows, set: null, ancestors, isArtificial);
    }

    /// <summary>
    ///  The flags of one display row, computing the rows above it first if needed; null
    ///  for an index outside the rows.
    /// </summary>
    public (bool Node, bool[] Segments)? At(int index)
    {
        EnsureUpTo(index);
        return index >= 0 && index < Flags.Count ? Flags[index] : null;
    }

    public void EnsureUpTo(int index)
    {
        for (int i = Flags.Count; i <= index && i < _rows.Count; i++)
        {
            Flags.Add(Step(i, _rows[i]));
        }
    }

    private (bool Node, bool[] Segments) Step(int index, RevisionRow row)
    {
        bool nodeRelative = _ancestors is not null
            ? index < _ancestors.Length && _ancestors[index] && !_isArtificial(row)
            : _isArtificial(row) || _set!.Count == 0 || _set.Contains(row.Hash);

        int nodeLane = row.NodeLane;
        IReadOnlyList<RevisionGraphSegment> segments = row.GraphSegments;
        bool[] flags = new bool[segments.Count];
        _outgoing.Clear();

        for (int s = 0; s < segments.Count; s++)
        {
            RevisionGraphSegment seg = segments[s];

            // An edge that LEAVES this node — its lower half starts at the node lane — is
            // relative because the node is; anything else inherits the flag its own edge
            // arrived with.
            bool leavesNode = seg.FromY >= 0.5 && (int)Math.Round(seg.FromLane) == nodeLane;
            bool relative = leavesNode
                ? nodeRelative || _incoming.Contains(seg.ColorLane)
                : _incoming.Contains(seg.ColorLane);

            flags[s] = relative;

            // Whatever reaches the bottom edge is what the next row inherits — full-height
            // segments included, which is why this asks about ToY rather than about the
            // half the segment lives in.
            if (relative && seg.ToY >= 1.0)
            {
                _outgoing.Add(seg.ColorLane);
            }
        }

        (_incoming, _outgoing) = (_outgoing, _incoming);
        return (nodeRelative, flags);
    }
}

/// <summary>
///  A list of revision rows with each row's parents resolved to row indices (parents that
///  are not loaded are dropped), plus the hash -> row lookup. Built once per row set and
///  reused by every ancestor walk over it.
/// </summary>
public sealed class RevisionParentIndex
{
    private readonly int[][] _parents;
    private readonly Dictionary<string, int> _rowOf;

    public RevisionParentIndex(IReadOnlyList<RevisionRow> rows)
    {
        _rowOf = new Dictionary<string, int>(rows.Count, StringComparer.Ordinal);
        for (int i = 0; i < rows.Count; i++)
        {
            _rowOf.TryAdd(rows[i].Hash, i);
        }

        _parents = new int[rows.Count][];
        for (int i = 0; i < rows.Count; i++)
        {
            IReadOnlyList<string> parents = rows[i].ParentHashes;
            if (parents.Count == 0)
            {
                _parents[i] = [];
                continue;
            }

            List<int> resolved = new(parents.Count);
            foreach (string parent in parents)
            {
                if (_rowOf.TryGetValue(parent, out int p))
                {
                    resolved.Add(p);
                }
            }

            _parents[i] = [.. resolved];
        }
    }

    /// <summary>The loaded parents of the row at <paramref name="row"/>.</summary>
    public ReadOnlySpan<int> ParentsOf(int row) => _parents[row];

    /// <summary>The row of a hash, or -1 when it is not loaded.</summary>
    public int RowOf(string hash) => _rowOf.TryGetValue(hash, out int row) ? row : -1;
}
