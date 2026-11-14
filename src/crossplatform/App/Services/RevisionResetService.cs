using System.Text;
using GitCommands;
using GitExtensions.Extensibility;
using GitExtensions.Extensibility.Git;

namespace GitExtensions.Avalonia.Services;

/// <summary>
///  "Reset file(s) to selected revision / to parent revision" from the changed-file
///  list of the revision diff: upstream's <c>FileStatusList.ResetSelectedItemsWithConfirmation</c>
///  without the UI. The git work is the shared core's <see cref="GitModule.ResetChanges"/>,
///  exactly as upstream calls it; this class only rebuilds the <see cref="GitItemStatus"/>
///  items the port's <see cref="DiffFileRow"/> does not carry, and answers the questions
///  the confirmation needs.
/// </summary>
public static class RevisionResetService
{
    /// <summary>
    ///  Whether <paramref name="hash"/> names a revision files can be checked out from:
    ///  a real commit, not the zero id of a root commit's absent parent and not one of
    ///  the artificial worktree/index sentinels. Upstream's <c>CanResetToSecond</c>.
    /// </summary>
    public static bool CanResetTo(string? hash)
        => hash is { Length: > 0 }
            && ObjectId.TryParse(hash, out ObjectId id)
            && !id.IsZeroOrArtificial;

    /// <summary>
    ///  The core's status item for a committed change. A COPY is reported as an added
    ///  file as well, because that is what it is at its own path (the same reading as
    ///  upstream fd1a1b0e7's <c>IsAdded</c>): resetting it to the parent must delete it,
    ///  not try to check out a path the parent never had.
    /// </summary>
    public static GitItemStatus ToItemStatus(DiffFileRow row)
        => new(row.Name)
        {
            OldName = row.OldName,
            IsTracked = row.IsTracked,
            IsNew = row.Kind is DiffChangeKind.Added or DiffChangeKind.Copied,
            IsCopied = row.Kind == DiffChangeKind.Copied,
            IsDeleted = row.Kind == DiffChangeKind.Deleted,
            IsRenamed = row.Kind == DiffChangeKind.Renamed,
            IsChanged = row.Kind == DiffChangeKind.Modified,
            Staged = StagedStatus.None,
        };

    /// <summary>
    ///  Checks whether resetting any of the items could delete a file from the working
    ///  directory, i.e. whether offering the option to delete new files can have any
    ///  effect. Upstream 52d08e996 (<c>FileStatusList.HasFilesWhichMayBeDeleted</c>): a
    ///  changed file is never deleted, nothing can be deleted for a path that does not
    ///  exist (a file deleted in the selected revision, which the reset restores), and a
    ///  rename is checked under both names because its name is inverted when resetting
    ///  to the selected revision.
    ///
    ///  <para>One deliberate narrowing: upstream tests the old name of ANY item, the
    ///  port only of a rename, which is what the upstream commit message describes. A
    ///  copy carries its source as old name too, and that source is never deleted by a
    ///  reset (it exists, so upstream would offer a checkbox with no effect, the very
    ///  thing the fix is about).</para>
    /// </summary>
    public static bool HasFilesWhichMayBeDeleted(IReadOnlyList<GitItemStatus> items, IFullPathResolver fullPathResolver)
    {
        foreach (GitItemStatus item in items)
        {
            if (item.IsChanged)
            {
                continue;
            }

            if (PathExists(item.Name) || (item.IsRenamed && item.OldName is string oldName && PathExists(oldName)))
            {
                return true;
            }
        }

        return false;

        bool PathExists(string name)
        {
            string? path = fullPathResolver.Resolve(name);
            return File.Exists(path) || Directory.Exists(path);
        }
    }

    /// <summary>
    ///  Resets <paramref name="items"/> to <paramref name="resetHash"/>. When
    ///  <paramref name="toParent"/> is <see langword="false"/> the items are the changes
    ///  INTO <paramref name="resetHash"/> and are inverted first, as upstream does: a file
    ///  added by the selected revision is then something to check out, a file it deleted
    ///  something that may be deleted.
    ///  Returns git's complaints, empty on success. Blocks; never call it from the UI thread.
    /// </summary>
    public static string Reset(
        string repoPath,
        string resetHash,
        IReadOnlyList<GitItemStatus> items,
        bool toParent,
        bool resetAndDelete)
    {
        if (!CanResetTo(resetHash) || items.Count == 0)
        {
            return string.Empty;
        }

        GitModule module = GitContext.CreateModule(repoPath);
        GitItemStatus[] resetItems = toParent ? [.. items] : [.. items.Select(item => item.InvertStatus())];
        module.ResetChanges(
            ObjectId.Parse(resetHash),
            resetItems,
            resetAndDelete,
            new FullPathResolver(() => repoPath),
            out StringBuilder output);

        return output.ToString().Trim();
    }
}
