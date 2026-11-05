# Brencho

<img src="/src/crossplatform/App/Assets/Icons/Brencho.svg" alt="" width="72" align="right">

**A Linux port of [Git Extensions](https://github.com/gitextensions/gitextensions).**
The git logic is upstream's, compiled unchanged for plain `net10.0`; the interface is
a new Avalonia UI, because the original is WinForms and WinForms does not leave Windows.

![The main window: revision graph, repository tabs, and the diff pane](/src/crossplatform/docs/screenshots/main-window.png)

## What it adds over the original

- **Several repositories in one window**, on a VS Code-style tab strip: previews,
  pinning, duplicated tabs, submodules and worktrees one click away.
- **Built-in diff, three-way merge and image comparison**, driven by git itself.
  No external merge tool has to be configured.
- **Theme that follows the desktop** (Light / Dark / System), live UI zoom, and
  vector icons coloured by what each command does.
- **An embedded terminal** with a pty of its own, so there is nothing to install.
- **A command palette** (`Ctrl+Shift+P`) built from the real menu.
- **An interactive-rebase todo shown as steps**, and a sequencer bar that tells you
  where a stopped operation is.
- **A `.deb` package** that needs no .NET on the target machine.

## Quick start (Linux)

```bash
cd src/crossplatform
dotnet build App/GitExtensions.Avalonia.csproj
./run.sh /path/to/a/git/repo
```

## Read more

| | |
|---|---|
| **[Full README](/src/crossplatform/README.md)** | Every difference from upstream with screenshots, what is missing, build on Linux and Windows |
| [ROADMAP.md](/src/crossplatform/ROADMAP.md) | Known gaps and what comes next |
| [PORTING.md](/src/crossplatform/PORTING.md) | The port's milestone-by-milestone record |
| [packaging/](/src/crossplatform/packaging/) | Building the `.deb` |
| [Upstream README](/README.md) | The original Git Extensions (Windows) project |

The whole port lives in [`src/crossplatform/`](/src/crossplatform/). Everything
else in this repository is upstream Git Extensions, kept in sync with it.

## License

[GPL v3](/LICENSE.md), the same as Git Extensions, from which this is derived. The
`.deb` carries the notices of every bundled third-party component in
`/usr/share/doc/brencho/copyright`.
