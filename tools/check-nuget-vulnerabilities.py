#!/usr/bin/env python3
"""Fail when dotnet list package JSON contains a package advisory."""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any
from urllib.parse import urlparse

SCOPES = ("topLevelPackages", "transitivePackages")
PACKAGE_FIELDS = {"id", "resolvedVersion", "vulnerabilities"}
TOP_LEVEL_PACKAGE_FIELDS = PACKAGE_FIELDS | {"requestedVersion"}
VULNERABILITY_FIELDS = {"severity", "advisoryurl"}
SEVERITIES = {"Low", "Moderate", "High", "Critical"}
MAX_BYTES = 10 * 1024 * 1024


def schema_error(source: str, detail: str) -> ValueError:
    return ValueError(f"{source}: invalid dotnet package report ({detail})")


def findings(document: Any, source: str) -> list[dict[str, Any]]:
    if not isinstance(document, dict):
        raise schema_error(source, "root must be an object")
    if set(document) - {"version", "parameters", "sources", "projects"}:
        raise schema_error(source, "root contains unknown fields")
    version = document.get("version")
    if isinstance(version, bool) or not isinstance(version, int) or version != 1:
        raise schema_error(source, "version must be 1")
    if "parameters" in document and not isinstance(document["parameters"], str):
        raise schema_error(source, "parameters must be a string")
    if "sources" in document:
        sources = document["sources"]
        if not isinstance(sources, list) or any(not isinstance(item, str) for item in sources):
            raise schema_error(source, "sources must be an array of strings")
    projects = document.get("projects")
    if not isinstance(projects, list) or not projects:
        raise schema_error(source, "projects must be a non-empty array")

    result: list[dict[str, Any]] = []
    project_paths: set[str] = set()
    for project in projects:
        if not isinstance(project, dict):
            raise schema_error(source, "project entry must be an object")
        if set(project) - {"path", "frameworks"}:
            raise schema_error(source, "project contains unknown fields")
        project_path = project.get("path")
        if not isinstance(project_path, str) or not project_path.strip():
            raise schema_error(source, "project path must be a non-empty string")
        if project_path in project_paths:
            raise schema_error(source, "project paths must be unique")
        project_paths.add(project_path)

        # dotnet omits frameworks entirely for a clean --vulnerable report.
        if "frameworks" not in project:
            continue
        frameworks = project["frameworks"]
        if not isinstance(frameworks, list) or not frameworks:
            raise schema_error(source, "frameworks must be a non-empty array when present")
        framework_names: set[str] = set()
        for framework in frameworks:
            if not isinstance(framework, dict):
                raise schema_error(source, "framework entry must be an object")
            if set(framework) - {"framework", *SCOPES}:
                raise schema_error(source, "framework contains unknown fields")
            framework_name = framework.get("framework")
            if not isinstance(framework_name, str) or not framework_name.strip():
                raise schema_error(source, "framework name must be a non-empty string")
            if framework_name in framework_names:
                raise schema_error(source, "framework names must be unique")
            framework_names.add(framework_name)

            for scope in SCOPES:
                if scope not in framework:
                    continue
                packages = framework[scope]
                if not isinstance(packages, list) or not packages:
                    raise schema_error(source, f"{scope} must be a non-empty array when present")
                for package in packages:
                    if not isinstance(package, dict):
                        raise schema_error(source, "package entry must be an object")
                    allowed_package_fields = (
                        TOP_LEVEL_PACKAGE_FIELDS if scope == "topLevelPackages" else PACKAGE_FIELDS
                    )
                    if set(package) - allowed_package_fields or not PACKAGE_FIELDS <= set(package):
                        raise schema_error(source, "package contains unknown or missing fields")
                    package_id = package["id"]
                    if not isinstance(package_id, str) or not package_id.strip():
                        raise schema_error(source, "package id must be a non-empty string")
                    if "requestedVersion" in package:
                        requested_version = package["requestedVersion"]
                        if not isinstance(requested_version, str) or not requested_version.strip():
                            raise schema_error(source, "package requestedVersion must be a non-empty string")
                    resolved_version = package["resolvedVersion"]
                    if not isinstance(resolved_version, str) or not resolved_version.strip():
                        raise schema_error(source, "package resolvedVersion must be a non-empty string")
                    vulnerabilities = package["vulnerabilities"]
                    if not isinstance(vulnerabilities, list) or not vulnerabilities:
                        raise schema_error(source, "vulnerabilities must be a non-empty array")
                    for vulnerability in vulnerabilities:
                        if not isinstance(vulnerability, dict):
                            raise schema_error(source, "vulnerability entries must be objects")
                        if set(vulnerability) != VULNERABILITY_FIELDS:
                            raise schema_error(source, "vulnerability must contain exactly severity and advisoryurl")
                        severity = vulnerability["severity"]
                        advisory_url = vulnerability["advisoryurl"]
                        if severity not in SEVERITIES:
                            raise schema_error(source, "vulnerability severity is invalid")
                        parsed_url = urlparse(advisory_url) if isinstance(advisory_url, str) else None
                        if (
                            not isinstance(advisory_url, str)
                            or not advisory_url.strip()
                            or advisory_url != advisory_url.strip()
                            or parsed_url is None
                            or parsed_url.scheme.lower() not in {"http", "https"}
                            or not parsed_url.netloc
                        ):
                            raise schema_error(source, "advisoryurl must be an absolute http(s) URL")
                    result.append(
                        {
                            "file": source,
                            "framework": framework_name,
                            "id": package_id,
                            "project": project_path,
                            "scope": scope,
                            "vulnerabilities": vulnerabilities,
                        }
                    )
    return result


def _reject_duplicate_keys(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON object key")
        result[key] = value
    return result


def read_json(path: Path) -> Any:
    # Read at most one byte beyond the contract before parsing.  This avoids
    # handing an unbounded stream to json.load and keeps oversized reports out
    # of parser memory/error paths.
    if path.is_dir():
        raise ValueError(f"{path}: report input must be a file, not a directory")
    with path.open("rb") as stream:
        data = stream.read(MAX_BYTES + 1)
    if len(data) > MAX_BYTES:
        raise ValueError(f"{path}: JSON report exceeds {MAX_BYTES} bytes")
    try:
        return json.loads(data.decode("utf-8"), object_pairs_hook=_reject_duplicate_keys)
    except (UnicodeDecodeError, json.JSONDecodeError, ValueError) as error:
        raise ValueError(f"{path}: invalid JSON report") from error


def main(arguments: list[str]) -> int:
    if not arguments:
        print(f"usage: {Path(sys.argv[0]).name} JSON [...JSON]", file=sys.stderr)
        return 2

    all_findings: list[dict[str, Any]] = []
    try:
        for name in arguments:
            all_findings.extend(findings(read_json(Path(name)), name))
    except (OSError, ValueError) as error:
        print(f"NuGet vulnerability audit could not parse input: {error}", file=sys.stderr)
        return 2

    all_findings.sort(
        key=lambda item: (
            item["file"], item["project"], item["framework"], item["scope"], item["id"]
        )
    )
    if all_findings:
        print(json.dumps(all_findings, sort_keys=True, separators=(",", ":")), file=sys.stderr)
        print(f"NuGet vulnerability audit failed: {len(all_findings)} vulnerable package(s).", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
