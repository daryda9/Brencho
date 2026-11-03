# Debian packaging (Linux/Avalonia port)

Building the `.deb` requires `dpkg-deb` (from `dpkg-dev`: `sudo apt install dpkg-dev`) the .NET 10 SDK on `PATH`, and `python3`. Run `./build-deb.sh` from this directory; it does a self-contained `dotnet publish` (so the target machine needs no .NET runtime), stages a Debian tree (payload under `/opt/gitnext`, a `/usr/bin/gitnext` launcher, a `.desktop` entry the product mark in every hicolor size (16 to 512), and the licence notices under `/usr/share/doc/gitnext/`), and produces `out/gitnext_<version>_amd64.deb`. Install it with `sudo dpkg -i out/gitnext_<version>_amd64.deb` (or `sudo apt install ./out/gitnext_<version>_amd64.deb` to pull the `git` dependency automatically), then launch from your app menu or by running `gitnext`.

## Licence notices

`/usr/share/doc/gitnext/copyright` is generated at build time by `gen-copyright.py`
from the publish's `GitNext.deps.json`: every shipped assembly and native library is
listed with its holder and licence, read from the package's `.nuspec` in the NuGet
cache. A package whose licence the script does not recognise **fails the build**; add
it to `SPDX` (for a licence expression) or, after reading the licence file it points
to, to `REVIEWED_LICENSE_FILES`. Code that a package embeds from someone else (Skia,
HarfBuzz, the Inter font) is declared in `EMBEDDED`. The licence texts quoted in full
live in `licenses/`; GPL-3 and Apache-2.0 point to `/usr/share/common-licenses/`.
`ICON-SOURCES.txt` beside it is upstream's list of where the Classic icons came from.
