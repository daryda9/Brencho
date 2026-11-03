#!/usr/bin/env python3
"""Write the Debian copyright file for the gitNext .deb.

Usage: gen-copyright.py <publish-dir> <output-file>

The third-party part is derived from <publish-dir>/GitNext.deps.json, i.e. from
what the publish actually shipped, so a new NuGet dependency cannot slip into the
package unattributed: its licence is read from the package's .nuspec in the
NuGet cache, and a licence this script does not recognise stops the build
instead of being written down as "unknown".

Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Paths in Files: are relative to /opt/gitnext, where the payload is installed.
"""

import json
import os
import re
import sys
from pathlib import Path

HERE = Path(__file__).resolve().parent
LICENSES = HERE / "licenses"
NUGET = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget" / "packages"))

# SPDX expression in a .nuspec -> licence name used in this file.
SPDX = {
    "MIT": "Expat",
    "Apache-2.0": "Apache-2.0",
    "BSD-3-Clause": "BSD-3-Clause",
}

# Packages whose .nuspec points at a licence *file* rather than an expression.
# Each entry was checked by reading that file; re-check when the version moves.
REVIEWED_LICENSE_FILES = {
    "StrongOf": "Expat",                        # LICENSE.txt: MIT, (c) 2023 Ben Abt
    "Avalonia.Angle.Windows.Natives": "BSD-3-Clause",  # ANGLE; Windows-only payload
}

# Files that carry more than their NuGet package says: third-party code or data
# compiled into them. Keyed by file name; adds copyright lines and licences.
EMBEDDED = {
    "libSkiaSharp.so": (["2005-2024 Google Inc. (Skia)"], ["BSD-3-Clause"]),
    "libHarfBuzzSharp.so": (["2005-2023 HarfBuzz contributors (Behdad Esfahbod, Google Inc., Red Hat Inc. and others)"],
                            ["HarfBuzz-MIT"]),
    "Avalonia.Fonts.Inter.dll": (["2016 The Inter Project Authors (https://github.com/rsms/inter)"], ["OFL-1.1"]),
}


def nuspec_info(pkg_id, version):
    folder = NUGET / pkg_id.lower() / version.lower()
    spec = folder / f"{pkg_id.lower()}.nuspec"
    if not spec.exists():
        sys.exit(f"gen-copyright: {spec} not found (restore the solution first)")
    text = spec.read_text(encoding="utf-8")

    def tag(name):
        m = re.search(rf"<{name}\b[^>]*>([^<]*)</{name}>", text)
        return m.group(1).strip().replace("&amp;", "&") if m else ""

    m = re.search(r'<license\s+type="(\w+)"[^>]*>([^<]*)</license>', text)
    if m and m.group(1) == "expression":
        expr = m.group(2).strip()
        if expr not in SPDX:
            sys.exit(f"gen-copyright: {pkg_id} {version}: licence '{expr}' is not mapped in SPDX")
        lic = SPDX[expr]
    elif pkg_id in REVIEWED_LICENSE_FILES:
        lic = REVIEWED_LICENSE_FILES[pkg_id]
    else:
        sys.exit(f"gen-copyright: {pkg_id} {version}: no licence expression; read the package's "
                 f"licence file and add it to REVIEWED_LICENSE_FILES")

    holder = tag("copyright") or tag("authors")
    holder = re.sub(r"^(Copyright\s*)?(\(c\)|©)?\s*", "", holder, flags=re.I).strip()
    return holder or pkg_id, lic


def license_paragraph(name, path):
    body = []
    for line in path.read_text(encoding="utf-8").rstrip("\n").splitlines():
        body.append(" " + line if line.strip() else " .")
    return f"License: {name}\n" + "\n".join(body)


def main():
    publish, out = Path(sys.argv[1]), Path(sys.argv[2])
    deps = json.loads((publish / "GitNext.deps.json").read_text(encoding="utf-8"))
    target = next(v for k, v in deps["targets"].items() if "/" in k)  # the RID-specific one

    stanzas = []
    used = {"GPL-3", "ISC", "Expat", "CC-BY-3.0"}

    for key, entry in sorted(target.items(), key=lambda kv: kv[0].lower()):
        name, version = key.split("/", 1)
        kind = deps["libraries"].get(key, {}).get("type")
        files = sorted({Path(p).name for sect in ("runtime", "native") for p in entry.get(sect, {})})
        files = [f for f in files if (publish / f).exists()]
        if not files:
            continue

        if kind in ("project", "reference"):
            continue  # this repository's own assemblies: the Files: * stanza (GPL-3)
        if kind not in ("package", "runtimepack"):
            sys.exit(f"gen-copyright: {key}: library type '{kind}' is not handled")
        if name.startswith("runtimepack.Microsoft.NETCore.App.Runtime"):
            holder, lic = ".NET Foundation and Contributors", "Expat"
            comment = f".NET runtime {version}, shipped because the package is self-contained."
        else:
            holder, lic = nuspec_info(name, version)
            comment = f"NuGet package {name} {version}."

        if name.startswith("runtimepack."):
            stanzas.append((files, [holder], [lic], comment))
            used.add(lic)
            continue

        plain = [f for f in files if f not in EMBEDDED]
        if plain:
            stanzas.append((plain, [holder], [lic], comment))
            used.add(lic)
        for f in files:
            if f in EMBEDDED:
                extra_holders, extra_lics = EMBEDDED[f]
                stanzas.append(([f], [holder, *extra_holders], [lic, *extra_lics],
                                comment + " Embeds third-party code or fonts; every licence listed applies."))
                used.update([lic, *extra_lics])

    with out.open("w", encoding="utf-8") as w:
        w.write(
            "Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/\n"
            "Upstream-Name: gitNext\n"
            "Source: https://github.com/daryda9/gitextensions\n"
            "Comment: gitNext is a Linux port of Git Extensions\n"
            " (https://github.com/gitextensions/gitextensions), modified by the gitNext\n"
            " contributors. The complete corresponding source is at the Source: address.\n"
            " Paths below are relative to /opt/gitnext.\n\n"
            "Files: *\n"
            "Copyright: 2008-2026 Git Extensions Team\n"
            "           2025-2026 gitNext contributors\n"
            "License: GPL-3\n\n"
            "Files: GitNext.dll\n"
            "Copyright: 2008-2026 Git Extensions Team\n"
            "           2025-2026 gitNext contributors\n"
            "           2013-2022 Cole Bemis (Feather)\n"
            "           2022 Lucide Contributors\n"
            "           Yusuke Kamiyamane (Fugue icons) and the other authors listed in ICON-SOURCES.txt\n"
            "License: GPL-3 and ISC and CC-BY-3.0\n"
            "Comment: The application code is GPL-3. The Modern icon set is transcribed\n"
            " from Lucide (lucide-react 0.462.0), ISC; portions of Lucide come from\n"
            " Feather, MIT. The Classic icon set is upstream Git Extensions' bitmap set,\n"
            " mostly from Fugue by Yusuke Kamiyamane (CC-BY-3.0); its full list of\n"
            " sources and terms is /usr/share/doc/gitnext/ICON-SOURCES.txt.\n\n"
        )
        for files, holders, lics, comment in stanzas:
            w.write("Files: " + "\n       ".join(files) + "\n")
            w.write("Copyright: " + "\n           ".join(holders) + "\n")
            w.write("License: " + " and ".join(lics) + "\n")
            w.write(f"Comment: {comment}\n\n")

        w.write("License: GPL-3\n"
                " On Debian systems the full text of the GNU General Public License\n"
                " version 3 is in /usr/share/common-licenses/GPL-3.\n\n")
        if "Apache-2.0" in used:
            w.write("License: Apache-2.0\n"
                    " On Debian systems the full text of the Apache License 2.0 is in\n"
                    " /usr/share/common-licenses/Apache-2.0.\n\n")
        w.write("License: CC-BY-3.0\n"
                " Creative Commons Attribution 3.0:\n"
                " https://creativecommons.org/licenses/by/3.0/legalcode\n\n")
        for name in ("Expat", "ISC", "BSD-3-Clause", "HarfBuzz-MIT", "OFL-1.1"):
            if name in used:
                w.write(license_paragraph(name, LICENSES / name) + "\n\n")


if __name__ == "__main__":
    main()
