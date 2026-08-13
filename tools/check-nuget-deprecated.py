#!/usr/bin/env python3
"""Fail when dotnet list package JSON reports deprecated packages."""

from __future__ import annotations

import json
import sys
from pathlib import Path
from typing import Any

SCOPES = ("topLevelPackages", "transitivePackages")
PACKAGE_FIELDS = {"id", "resolvedVersion", "deprecationReasons"}
TOP_LEVEL_PACKAGE_FIELDS = PACKAGE_FIELDS | {"requestedVersion"}
ALTERNATIVE_PACKAGE_FIELDS = {"id", "versionRange"}
MAX_BYTES = 10 * 1024 * 1024


def schema_error(source: str, detail: str) -> ValueError:
    return ValueError(f"{source}: invalid dotnet package report ({detail})")


def findings(document: Any, source: str) -> list[dict[str, str]]:
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

    result: list[dict[str, str]] = []
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
        # dotnet omits frameworks entirely for a clean --deprecated report.
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
                        TOP_LEVEL_PACKAGE_FIELDS.copy()
                        if scope == "topLevelPackages"
                        else PACKAGE_FIELDS.copy()
                    )
                    if "alternativePackage" in package:
                        allowed_package_fields.add("alternativePackage")
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
                    reasons = package["deprecationReasons"]
                    if (
                        not isinstance(reasons, list)
                        or not reasons
                        or any(not isinstance(reason, str) or not reason.strip() for reason in reasons)
                    ):
                        raise schema_error(source, "deprecationReasons must be a non-empty array of strings")
                    if "alternativePackage" in package:
                        alternative = package["alternativePackage"]
                        if (
                            not isinstance(alternative, dict)
                            or set(alternative) != ALTERNATIVE_PACKAGE_FIELDS
                            or not isinstance(alternative["id"], str)
                            or not alternative["id"].strip()
                            or not isinstance(alternative["versionRange"], str)
                            or not alternative["versionRange"].strip()
                        ):
                            raise schema_error(source, "alternativePackage must contain non-empty id and versionRange")

                    # A package object in a filtered deprecated report is itself
                    # evidence.  Never let a malformed or metadata-free entry
                    # silently become a clean report.
                    result.append(
                        {
                            "file": source,
                            "framework": framework_name,
                            "id": package_id,
                            "project": project_path,
                            "scope": scope,
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

    all_findings: list[dict[str, str]] = []
    try:
        for argument in arguments:
            path = Path(argument)
            all_findings.extend(findings(read_json(path), str(path)))
    except (OSError, ValueError) as error:
        print(f"NuGet deprecated-package audit could not parse input: {error}", file=sys.stderr)
        return 2

    all_findings.sort(key=lambda item: (item["file"], item["project"], item["framework"], item["scope"], item["id"]))
    if all_findings:
        print(json.dumps(all_findings, sort_keys=True, separators=(",", ":")), file=sys.stderr)
        print(f"NuGet deprecated-package audit failed: {len(all_findings)} package(s).", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
