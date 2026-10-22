#!/usr/bin/env python3
"""Patch only the pinned producer's empty-release result before building it."""

import argparse
import sys
from pathlib import Path


BEFORE = b'''            if (releases.Count == 0)
            {
                Serilog.Log.Information($"Found no releases for {owner}/{name}");
                return null;
            }'''

AFTER = b'''            if (releases.Count == 0)
            {
                Serilog.Log.Warning($"Found no releases for {owner}/{name}; continuing to published-history reconciliation and expected-release verification.");
                return new List<string>();
            }'''


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=Path, help="Path to pinned ci/PackageBuilder/Build.cs")
    args = parser.parse_args()
    content = args.source.read_bytes()
    newline = b"\r\n" if b"\r\n" in content else b"\n"
    before = BEFORE.replace(b"\n", newline)
    after = AFTER.replace(b"\n", newline)
    count = content.count(before)
    if count != 1:
        raise ValueError(f"Expected exactly one pinned empty-releases block; found {count}. Source was not changed.")
    args.source.write_bytes(content.replace(before, after, 1))
    print("Patched one empty-releases block; other null results and HTTP exceptions are unchanged.")


if __name__ == "__main__":
    try:
        main()
    except (OSError, ValueError) as error:
        print(f"::error::Listing builder patch refused: {error}", file=sys.stderr)
        sys.exit(1)
