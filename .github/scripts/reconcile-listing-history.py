#!/usr/bin/env python3
"""Retain verified published history when GitHub release discovery is incomplete."""

import argparse
import copy
import json
import re
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from urllib.parse import urlsplit


def load(path):
    return json.loads(Path(path).read_text(encoding="utf-8-sig"))


def validate(listing, source, label):
    if not isinstance(listing, dict):
        raise ValueError(f"{label}: listing is not a JSON object")
    for field in ("id", "name", "url"):
        if not source.get(field) or listing.get(field) != source[field]:
            raise ValueError(f"{label}: listing identity differs from source.json ({field})")
    groups = listing.get("packages")
    if not isinstance(groups, dict) or not groups:
        raise ValueError(f"{label}: package listing is empty or malformed")
    repositories = set(source.get("githubRepos", []))
    explicit_urls = {url for package in source.get("packages", []) for url in package.get("releases", [])}
    entries = {}
    for package, group in groups.items():
        versions = group.get("versions") if isinstance(group, dict) else None
        if not isinstance(versions, dict) or not versions:
            raise ValueError(f"{label}: missing versions for {package}")
        for version, manifest in versions.items():
            if not isinstance(manifest, dict) or manifest.get("name") != package or manifest.get("version") != version:
                raise ValueError(f"{label}: manifest identity differs from its package/version key: {package}@{version}")
            digest = manifest.get("zipSHA256", "")
            if not isinstance(digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", digest):
                raise ValueError(f"{label}: invalid ZIP SHA-256: {package}@{version}")
            url = manifest.get("url", "")
            if not isinstance(url, str):
                raise ValueError(f"{label}: invalid asset URL: {package}@{version}")
            parts = urlsplit(url)
            segments = parts.path.strip("/").split("/")
            configured_release = (parts.scheme == "https" and parts.netloc == "github.com"
                and not parts.query and not parts.fragment and len(segments) >= 6
                and "/".join(segments[:2]) in repositories and segments[2:4] == ["releases", "download"])
            if not configured_release and url not in explicit_urls:
                raise ValueError(f"{label}: asset URL is outside configured release sources: {package}@{version}")
            entries[(package, version)] = manifest
    return entries


def reconcile(source, generated, live, expected_package="", expected_version="", expected_sha256=""):
    fresh = validate(generated, source, "generated")
    published = validate(live, source, "live")
    expected = (expected_package, expected_version, expected_sha256)
    if any(expected):
        if not all(expected) or not re.fullmatch(r"[0-9a-fA-F]{64}", expected_sha256):
            raise ValueError("Expected package, version, and SHA-256 must all be supplied and valid")
        manifest = fresh.get((expected_package, expected_version))
        if manifest is None or manifest["zipSHA256"].lower() != expected_sha256.lower():
            raise ValueError("Expected release is absent from freshly generated metadata or has a different ZIP SHA-256")
    merged = copy.deepcopy(generated)
    retained = 0
    for (package, version), old in published.items():
        current = fresh.get((package, version))
        if current is not None:
            for field in ("name", "version", "url", "zipSHA256"):
                left, right = old[field], current[field]
                if field == "zipSHA256":
                    left, right = left.lower(), right.lower()
                if left != right:
                    raise ValueError(f"Immutable published release conflict ({field}): {package}@{version}")
        else:
            retained += 1
        if package not in merged["packages"]:
            merged["packages"][package] = copy.deepcopy(live["packages"][package])
        merged["packages"][package]["versions"][version] = copy.deepcopy(old)
    combined = validate(merged, source, "reconciled")
    if not published.keys() <= combined.keys() or any(combined[key] != old for key, old in published.items()):
        raise ValueError("Published history changed during reconciliation")
    return merged, {"generated": len(fresh), "live": len(published), "retained": retained, "total": len(combined)}


def fetch_live(url):
    for attempt in range(3):
        try:
            request = urllib.request.Request(url, headers={"User-Agent": "AjisaiFlow-VPM-Listing", "Cache-Control": "no-cache"})
            with urllib.request.urlopen(request, timeout=30) as response:
                return json.load(response)
        except (urllib.error.URLError, OSError, ValueError):
            if attempt == 2:
                raise
            time.sleep(2 * (attempt + 1))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source", required=True)
    parser.add_argument("--generated", required=True)
    parser.add_argument("--live-file", help="Read the published listing snapshot locally for validation")
    parser.add_argument("--expected-package", default="")
    parser.add_argument("--expected-version", default="")
    parser.add_argument("--expected-sha256", default="")
    args = parser.parse_args()
    source = load(args.source)
    generated_path = Path(args.generated)
    live = load(args.live_file) if args.live_file else fetch_live(source["url"])
    merged, counts = reconcile(source, load(generated_path), live, args.expected_package, args.expected_version, args.expected_sha256)
    # Replace only the generated file, after all identity, conflict, and history checks pass.
    temporary = generated_path.with_name(generated_path.name + ".reconciled.tmp")
    with temporary.open("x", encoding="utf-8", newline="\n") as output:
        json.dump(merged, output, ensure_ascii=False, indent=2)
        output.write("\n")
    temporary.replace(generated_path)
    print("Reconciled listing: " + json.dumps(counts, sort_keys=True))


if __name__ == "__main__":
    try:
        main()
    except (KeyError, TypeError, ValueError, OSError) as error:
        print(f"::error::Listing reconciliation refused: {error}", file=sys.stderr)
        sys.exit(1)
