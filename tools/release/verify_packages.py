#!/usr/bin/env python3
"""Qualify exact local Bpmn 0.5 package artifacts with an isolated consumer."""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import zipfile
from pathlib import Path
from typing import Any
from xml.etree import ElementTree as ET
from xml.sax.saxutils import escape


PACKAGE_DEPENDENCIES: dict[str, list[str]] = {
    "Bpmn.Model": [],
    "Bpmn.Interchange": ["Bpmn.Model"],
    "Bpmn.Semantics": ["Bpmn.Model"],
    "Bpmn.Runtime.InMemory": ["Bpmn.Semantics"],
}
TARGET_FRAMEWORKS = {"net8.0", "net10.0"}
PAYLOAD_SCHEMA_ID = "https://valence.works/schemas/bpmn/payload/"
EXPECTED_REPOSITORY_URL = "https://github.com/valence-works/bpmn"
EXPECTED_ROOTS = {
    "BpmnDefinitions": "#/$defs/bpmnDefinitions",
    "BpmnExecutionState": "#/$defs/bpmnExecutionState",
    "BpmnWorkBinding": "#/$defs/bpmnWorkBinding",
}
EXPECTED_BINDING_DEFS = {
    "timerDateWait": "#/$defs/timerDateWait",
    "timerCycleWait": "#/$defs/timerCycleWait",
    "signalPublish": "#/$defs/signalPublish",
}


class QualificationError(Exception):
    pass


def local_name(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def child(element: ET.Element | None, name: str) -> ET.Element | None:
    if element is None:
        return None
    return next((item for item in element if local_name(item.tag) == name), None)


def text_child(element: ET.Element | None, name: str) -> str | None:
    item = child(element, name)
    return item.text.strip() if item is not None and item.text else None


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def parse_nuspec(archive: zipfile.ZipFile, archive_path: Path) -> dict[str, Any]:
    candidates = [name for name in archive.namelist() if local_name(Path(name).name).lower().endswith(".nuspec")]
    if len(candidates) != 1:
        raise QualificationError(f"{archive_path}: expected one nuspec, found {len(candidates)}")

    try:
        root = ET.fromstring(archive.read(candidates[0]))
    except (ET.ParseError, KeyError) as error:
        raise QualificationError(f"{archive_path}: cannot parse nuspec: {error}") from error

    metadata = child(root, "metadata")
    if metadata is None:
        raise QualificationError(f"{archive_path}: nuspec has no metadata element")

    repository = child(metadata, "repository")
    repository_commit = None
    repository_url = None
    repository_type = None
    if repository is not None:
        repository_commit = next((value for key, value in repository.attrib.items() if local_name(key) == "commit"), None)
        repository_url = next((value for key, value in repository.attrib.items() if local_name(key) == "url"), None)
        repository_type = next((value for key, value in repository.attrib.items() if local_name(key) == "type"), None)

    dependencies_element = child(metadata, "dependencies")
    groups: list[dict[str, Any]] = []
    if dependencies_element is not None:
        dependency_groups = [item for item in dependencies_element if local_name(item.tag) == "group"]
        if dependency_groups:
            for group in dependency_groups:
                framework = next((value for key, value in group.attrib.items() if local_name(key) == "targetFramework"), "")
                groups.append({"targetFramework": framework, "dependencies": read_dependencies(group)})
        else:
            groups.append({"targetFramework": "", "dependencies": read_dependencies(dependencies_element)})

    return {
        "id": text_child(metadata, "id"),
        "version": text_child(metadata, "version"),
        "repository": {
            "type": repository_type,
            "url": repository_url,
            "commit": repository_commit,
        },
        "dependencyGroups": groups,
        "nuspecPath": candidates[0],
    }


def read_dependencies(element: ET.Element) -> list[dict[str, str | None]]:
    result: list[dict[str, str | None]] = []
    for item in element:
        if local_name(item.tag) != "dependency":
            continue
        result.append({
            "id": next((value for key, value in item.attrib.items() if local_name(key) == "id"), None),
            "version": next((value for key, value in item.attrib.items() if local_name(key) == "version"), None),
        })
    return result


def normalize_tfm(value: str) -> str | None:
    compact = value.strip().lower().replace(" ", "")
    compact = compact.replace(".netcoreapp,version=v", "net").replace(".net,version=v", "net")
    if compact in {"net8.0", "net10.0"}:
        return compact
    match = re.fullmatch(r"net(?:coreapp)?(?:,version=v)?(8|10)\.0", compact)
    if match:
        return f"net{match.group(1)}.0"
    return None


def version_range_matches(value: str | None, version: str) -> bool:
    if value is None:
        return False
    value = value.strip()
    if value == version:
        return True
    if len(value) < 3 or value[0] not in "[(" or value[-1] not in ")]":
        return False
    body = value[1:-1]
    if "," not in body:
        return value[0] == "[" and value[-1] == "]" and body == version
    lower, upper = body.split(",", 1)
    if lower.strip() != version or value[0] != "[":
        return False
    if upper.strip() == "":
        return value[-1] == ")"
    return upper.strip() == version and value[-1] == "]"


def check_dependency_graph(metadata: dict[str, Any], package_id: str, version: str) -> None:
    expected = sorted(PACKAGE_DEPENDENCIES[package_id], key=str.casefold)
    groups = metadata["dependencyGroups"]
    if not expected:
        actual = [dependency for group in groups for dependency in group["dependencies"]]
        if actual:
            raise QualificationError(f"{package_id}: expected no shipped dependencies, found {actual}")
        return

    if not groups:
        raise QualificationError(f"{package_id}: nuspec omits expected dependencies {expected}")

    observed_frameworks: set[str] = set()
    for group in groups:
        raw_tfm = group["targetFramework"]
        if raw_tfm:
            tfm = normalize_tfm(raw_tfm)
            if tfm is None or tfm not in TARGET_FRAMEWORKS:
                raise QualificationError(f"{package_id}: dependency group targets unexpected framework {raw_tfm!r}")
            observed_frameworks.add(tfm)
        actual_ids = sorted((dependency["id"] or "" for dependency in group["dependencies"]), key=str.casefold)
        if actual_ids != expected:
            raise QualificationError(f"{package_id}: dependency group {raw_tfm or '(all frameworks)'} has {actual_ids}; expected {expected}")
        for dependency in group["dependencies"]:
            if not version_range_matches(dependency["version"], version):
                raise QualificationError(
                    f"{package_id}: dependency {dependency['id']} has version {dependency['version']!r}; "
                    f"expected exact {version} or [{version}, )"
                )

    if observed_frameworks and observed_frameworks != TARGET_FRAMEWORKS:
        raise QualificationError(f"{package_id}: dependency groups cover {sorted(observed_frameworks)}, expected both {sorted(TARGET_FRAMEWORKS)}")
    if not observed_frameworks and len(groups) != 1:
        raise QualificationError(f"{package_id}: unscoped dependency groups are ambiguous: {groups}")


def package_candidates(feed: Path, package_id: str, version: str, suffix: str) -> list[Path]:
    expected_name = f"{package_id}.{version}.{suffix}".casefold()
    return sorted((path for path in feed.rglob(f"*.{suffix}") if path.name.casefold() == expected_name), key=str)


def inspect_archive(path: Path, package_id: str, version: str, commit: str, kind: str) -> dict[str, Any]:
    try:
        with zipfile.ZipFile(path) as archive:
            corrupt_entry = archive.testzip()
            if corrupt_entry:
                raise QualificationError(f"{path}: corrupt archive member {corrupt_entry}")
            metadata = parse_nuspec(archive, path)
            if kind == "snupkg":
                archive_names = {name.lower() for name in archive.namelist()}
                expected_symbols = {
                    f"lib/{framework}/{package_id}.pdb".lower()
                    for framework in TARGET_FRAMEWORKS
                }
                missing_symbols = sorted(expected_symbols - archive_names)
                if missing_symbols:
                    raise QualificationError(
                        f"{path}: symbol package omits expected net8.0/net10.0 PDBs {missing_symbols}"
                    )
            if metadata["id"] != package_id:
                raise QualificationError(f"{path}: nuspec id is {metadata['id']!r}, expected {package_id!r}")
            if metadata["version"] != version:
                raise QualificationError(f"{path}: nuspec version is {metadata['version']!r}, expected {version!r}")
            packaged_commit = metadata["repository"]["commit"]
            if not isinstance(packaged_commit, str) or packaged_commit.lower() != commit.lower():
                raise QualificationError(
                    f"{path}: nuspec repository commit is {packaged_commit!r}, expected {commit!r}"
                )
            if metadata["repository"]["type"] != "git":
                raise QualificationError(f"{path}: unexpected repository type {metadata['repository']['type']!r}")
            if metadata["repository"]["url"] != EXPECTED_REPOSITORY_URL:
                raise QualificationError(
                    f"{path}: nuspec repository URL is {metadata['repository']['url']!r}, expected {EXPECTED_REPOSITORY_URL!r}"
                )
            result = {
                "kind": kind,
                "path": str(path.resolve()),
                "sizeBytes": path.stat().st_size,
                "sha256": sha256(path),
                "nuspec": metadata,
            }
            if kind == "nupkg":
                check_dependency_graph(metadata, package_id, version)
                lib_tfms = {
                    parts[1]
                    for name in archive.namelist()
                    if (parts := name.split("/")) and len(parts) >= 3 and parts[0].lower() == "lib" and parts[1]
                }
                if lib_tfms != TARGET_FRAMEWORKS:
                    raise QualificationError(f"{package_id}: packaged lib TFMs are {sorted(lib_tfms)}, expected {sorted(TARGET_FRAMEWORKS)}")
                archive_names = {name.lower() for name in archive.namelist()}
                missing_assemblies = [
                    f"lib/{framework}/{package_id}.dll".lower()
                    for framework in TARGET_FRAMEWORKS
                    if f"lib/{framework}/{package_id}.dll".lower() not in archive_names
                ]
                if missing_assemblies:
                    raise QualificationError(f"{package_id}: package omits expected assemblies {missing_assemblies}")
                result["targetFrameworks"] = sorted(lib_tfms)
                if package_id == "Bpmn.Model":
                    result["schema"] = inspect_model_schema(archive)
            return result
    except zipfile.BadZipFile as error:
        raise QualificationError(f"{path}: not a valid ZIP package: {error}") from error


def inspect_model_schema(archive: zipfile.ZipFile) -> dict[str, Any]:
    entries = [name for name in archive.namelist() if name.rstrip("/").lower() == "schema/bpmn-payload.schema.json"]
    if len(entries) != 1:
        raise QualificationError(f"Bpmn.Model: expected one packaged schema/bpmn-payload.schema.json, found {len(entries)}")
    try:
        schema = json.loads(archive.read(entries[0]))
    except (json.JSONDecodeError, KeyError) as error:
        raise QualificationError(f"Bpmn.Model: cannot parse packaged payload schema: {error}") from error

    version = schema.get("x-payloadFormatVersion")
    if version != "1.10.0":
        raise QualificationError(f"Bpmn.Model: candidate payload schema version is {version!r}; this release requires 1.10.0")
    expected_id = f"{PAYLOAD_SCHEMA_ID}{version}.json"
    if schema.get("$id") != expected_id:
        raise QualificationError(f"Bpmn.Model: schema id {schema.get('$id')!r} does not match payload version {version}")
    if schema.get("x-roots") != EXPECTED_ROOTS:
        raise QualificationError(f"Bpmn.Model: schema roots are {schema.get('x-roots')!r}, expected {EXPECTED_ROOTS!r}")
    one_of = schema.get("oneOf", [])
    actual_roots = [item.get("$ref") for item in one_of if isinstance(item, dict)]
    if actual_roots != list(EXPECTED_ROOTS.values()):
        raise QualificationError(f"Bpmn.Model: schema oneOf roots are {actual_roots!r}, expected {list(EXPECTED_ROOTS.values())!r}")

    definitions = schema.get("$defs", {})
    binding_union = definitions.get("bpmnWorkBinding", {}).get("oneOf", [])
    binding_refs = {item.get("$ref") for item in binding_union if isinstance(item, dict)}
    missing_binding_refs = set(EXPECTED_BINDING_DEFS.values()) - binding_refs
    if missing_binding_refs:
        raise QualificationError(f"Bpmn.Model: binding schema omits supported cases {sorted(missing_binding_refs)}")

    foreign_attribute = definitions.get("bpmnForeignAttribute", {})
    properties = foreign_attribute.get("properties", {})
    required = foreign_attribute.get("required", [])
    if version == "1.10.0":
        value_namespaces = properties.get("valueNamespaces")
        if value_namespaces is None or "valueNamespaces" in required:
            raise QualificationError("Bpmn.Model: payload 1.10.0 schema must expose valueNamespaces as optional metadata")
        if value_namespaces.get("type") != ["array", "null"] or value_namespaces.get("items") != {
            "$ref": "#/$defs/bpmnNamespaceBinding"
        }:
            raise QualificationError("Bpmn.Model: valueNamespaces must be a nullable array of bpmnNamespaceBinding records")
        namespace_binding = definitions.get("bpmnNamespaceBinding")
        if namespace_binding is None:
            raise QualificationError("Bpmn.Model: payload 1.10.0 schema omits bpmnNamespaceBinding")
        binding_properties = namespace_binding.get("properties", {})
        if "prefix" not in binding_properties or "ns" not in binding_properties:
            raise QualificationError("Bpmn.Model: bpmnNamespaceBinding must describe prefix and ns")
        if binding_properties["prefix"].get("type") != "string" or "prefix" not in namespace_binding.get("required", []):
            raise QualificationError("Bpmn.Model: bpmnNamespaceBinding.prefix must be required text")
        if binding_properties["ns"].get("type") != ["string", "null"]:
            raise QualificationError("Bpmn.Model: bpmnNamespaceBinding.ns must permit an observed unbound prefix")
        if "name" not in required or "value" not in required:
            raise QualificationError("Bpmn.Model: existing foreign-attribute name/value fields must remain required")

    return {
        "path": entries[0],
        "id": schema["$id"],
        "payloadFormatVersion": version,
        "roots": schema["x-roots"],
        "bindingCases": sorted(binding_refs),
        "qnameValueMetadata": version == "1.10.0",
    }


def write_consumer_files(directory: Path, feed: Path, version: str, package_consumer_source: Path) -> tuple[Path, Path]:
    project = directory / "PackageConsumer.csproj"
    package_refs = "\n".join(
        f'    <PackageReference Include="{escape(package_id)}" Version="[{escape(version)}]" />'
        for package_id in PACKAGE_DEPENDENCIES
    )
    project.write_text(
        """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <UseAppHost>false</UseAppHost>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
""" + package_refs + """
  </ItemGroup>
</Project>
""",
        encoding="utf-8",
    )
    config = directory / "NuGet.Config"
    escaped_feed = escape(str(feed), {'"': "&quot;"})
    config.write_text(
        f'''<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="candidate-feed" value="{escaped_feed}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="candidate-feed">
      <package pattern="Bpmn.*" />
    </packageSource>
    <packageSource key="nuget.org">
      <package pattern="Microsoft.NETCore.App.Ref" />
      <package pattern="Microsoft.AspNetCore.App.Ref" />
      <package pattern="Microsoft.NETCore.App.Host.*" />
    </packageSource>
  </packageSourceMapping>
</configuration>
''',
        encoding="utf-8",
    )
    shutil.copyfile(package_consumer_source, directory / "PackageConsumer.cs")
    return project, config


def run_command(
    command: list[str],
    cwd: Path,
    env: dict[str, str],
    log_path: Path,
    timeout_seconds: int,
) -> dict[str, Any]:
    started = time.time()
    try:
        completed = subprocess.run(
            command,
            cwd=cwd,
            env=env,
            text=True,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            timeout=timeout_seconds,
            check=False,
        )
        output = completed.stdout
        status = "passed" if completed.returncode == 0 else "failed"
        result: dict[str, Any] = {
            "status": status,
            "exitCode": completed.returncode,
            "durationSeconds": round(time.time() - started, 3),
        }
    except FileNotFoundError:
        output = "dotnet executable was not found on PATH."
        result = {"status": "missing-sdk", "exitCode": None, "durationSeconds": round(time.time() - started, 3)}
    except subprocess.TimeoutExpired as error:
        partial_output = error.stdout or ""
        if isinstance(partial_output, bytes):
            partial_output = partial_output.decode("utf-8", errors="replace")
        output = partial_output + "\nCommand timed out."
        result = {"status": "timed-out", "exitCode": None, "durationSeconds": round(time.time() - started, 3)}
    log_path.write_text(output, encoding="utf-8", errors="replace")
    result["command"] = command
    result["log"] = str(log_path.resolve())
    return result


def parse_consumer_result(log_path: Path) -> dict[str, Any] | None:
    marker = "QUALIFICATION_RESULT_JSON="
    for line in reversed(log_path.read_text(encoding="utf-8", errors="replace").splitlines()):
        if line.startswith(marker):
            try:
                parsed = json.loads(line[len(marker):])
                return parsed if isinstance(parsed, dict) else None
            except json.JSONDecodeError:
                return None
    return None


def ensure_evidence_outside_repo(path: Path, repository: Path) -> Path:
    resolved = path.resolve()
    try:
        resolved.relative_to(repository.resolve())
    except ValueError:
        return resolved
    raise QualificationError(f"Evidence directory must be outside the repository: {resolved}")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--packages", required=True, type=Path, help="Absolute path to the local package feed")
    parser.add_argument("--version", required=True, help="Exact candidate package version, for example 0.5.0")
    parser.add_argument("--commit", required=True, help="Exact 40-character repository commit recorded in every nuspec")
    parser.add_argument("--evidence-dir", type=Path, help="External directory for logs, receipt, project, and compiled consumer")
    parser.add_argument("--timeout-seconds", type=int, default=900, help="Per-command timeout (default: 900)")
    args = parser.parse_args()

    repository = Path(__file__).resolve().parents[2]
    feed = args.packages.resolve()
    evidence_created = args.evidence_dir is None
    evidence = Path(tempfile.mkdtemp(prefix="bpmn-package-qualification-")) if evidence_created else args.evidence_dir
    receipt_path: Path | None = None
    receipt: dict[str, Any] = {
        "tool": "Bpmn package consumer qualification",
        "status": "not-run",
        "packageVersion": args.version,
        "repositoryCommit": args.commit,
        "feed": str(feed),
        "packages": {},
        "consumer": {},
        "evidenceCreatedAutomatically": evidence_created,
    }
    evidence_safe = False

    try:
        if not args.packages.is_absolute() or not feed.is_dir():
            raise QualificationError(f"--packages must be an existing absolute directory: {args.packages}")
        if not re.fullmatch(r"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?", args.version):
            raise QualificationError(f"--version is not an exact semantic package version: {args.version!r}")
        if not re.fullmatch(r"[0-9a-fA-F]{40}", args.commit):
            raise QualificationError("--commit must be the exact 40-character repository commit SHA")
        if args.timeout_seconds < 1:
            raise QualificationError("--timeout-seconds must be positive")

        evidence = ensure_evidence_outside_repo(evidence, repository)
        if evidence.exists() and any(evidence.iterdir()):
            raise QualificationError(f"Evidence directory must be empty to preserve prior qualification records: {evidence}")
        evidence.mkdir(parents=True, exist_ok=True)
        evidence_safe = True
        receipt_path = evidence / "receipt.json"
        receipt["evidenceDirectory"] = str(evidence)
        receipt["consumerProject"] = str((evidence / "consumer").resolve())

        artifact_errors: list[str] = []
        for package_id in PACKAGE_DEPENDENCIES:
            package_records: dict[str, Any] = {}
            for suffix, kind in (("nupkg", "nupkg"), ("snupkg", "snupkg")):
                candidates = package_candidates(feed, package_id, args.version, suffix)
                if len(candidates) != 1:
                    artifact_errors.append(f"{package_id}: expected one {suffix}, found {[str(path) for path in candidates]}")
                    continue
                try:
                    package_records[kind] = inspect_archive(candidates[0], package_id, args.version, args.commit.lower(), kind)
                except QualificationError as error:
                    artifact_errors.append(str(error))
            receipt["packages"][package_id] = package_records

        if artifact_errors:
            receipt["packageInspection"] = {"status": "failed", "errors": artifact_errors}
            raise QualificationError("Package artifact inspection failed; see receipt.json for all findings.")
        receipt["packageInspection"] = {"status": "passed"}

        model_schema = receipt["packages"]["Bpmn.Model"]["nupkg"]["schema"]
        qname_metadata = model_schema["qnameValueMetadata"]
        payload_format_version = model_schema["payloadFormatVersion"]
        consumer_dir = evidence / "consumer"
        consumer_dir.mkdir()
        project, config = write_consumer_files(
            consumer_dir,
            feed,
            args.version,
            repository / "tools" / "release" / "PackageConsumer.cs",
        )
        cache = evidence / "nuget-cache"
        cache.mkdir()
        env = os.environ.copy()
        env.update({
            "NUGET_PACKAGES": str(cache),
            "DOTNET_CLI_TELEMETRY_OPTOUT": "1",
            "DOTNET_NOLOGO": "1",
            "NUGET_XMLDOC_MODE": "skip",
        })
        receipt["consumer"]["project"] = str(project.resolve())
        receipt["consumer"]["nugetConfig"] = str(config.resolve())
        receipt["consumer"]["packageCache"] = str(cache.resolve())
        receipt["consumer"]["source"] = str((consumer_dir / "PackageConsumer.cs").resolve())

        dotnet = shutil.which("dotnet")
        if dotnet is None:
            receipt["consumer"]["restore"] = {"status": "missing-sdk", "exitCode": None, "log": None}
            for framework in sorted(TARGET_FRAMEWORKS):
                receipt["consumer"].setdefault("frameworks", {})[framework] = {
                    "compile": {"status": "missing-sdk", "exitCode": None, "log": None},
                    "run": {"status": "not-run", "exitCode": None, "log": None},
                }
            raise QualificationError("dotnet SDK is missing; package inspection passed but consumer gates did not run.")

        sdk_log = evidence / "dotnet-sdks.log"
        sdk_result = run_command([dotnet, "--list-sdks"], evidence, env, sdk_log, args.timeout_seconds)
        receipt["consumer"]["installedSdks"] = sdk_result

        restore = run_command(
            [dotnet, "restore", str(project), "--configfile", str(config), "--packages", str(cache)],
            consumer_dir,
            env,
            evidence / "restore.log",
            args.timeout_seconds,
        )
        receipt["consumer"]["restore"] = restore
        if restore["status"] != "passed":
            for framework in sorted(TARGET_FRAMEWORKS):
                receipt["consumer"].setdefault("frameworks", {})[framework] = {
                    "compile": {"status": "not-run", "exitCode": None, "log": None},
                    "run": {"status": "not-run", "exitCode": None, "log": None},
                }
            raise QualificationError("Isolated package restore failed; compile and execution are not qualified.")

        for framework in ("net8.0", "net10.0"):
            compile_result = run_command(
                [dotnet, "build", str(project), "--framework", framework, "--configuration", "Release", "--no-restore"],
                consumer_dir,
                env,
                evidence / f"{framework}-compile.log",
                args.timeout_seconds,
            )
            framework_record: dict[str, Any] = {"compile": compile_result}
            if compile_result["status"] == "passed":
                executable = consumer_dir / "bin" / "Release" / framework / "PackageConsumer.dll"
                run_result = run_command(
                    [dotnet, "exec", str(executable)],
                    consumer_dir,
                    env,
                    evidence / f"{framework}-run.log",
                    args.timeout_seconds,
                )
                if run_result["status"] == "failed":
                    run_log = Path(run_result["log"]).read_text(encoding="utf-8", errors="replace").lower()
                    if "you must install or update .net" in run_log or "framework: 'microsoft.netcore.app'" in run_log:
                        run_result["status"] = "missing-runtime"
                framework_record["run"] = run_result
                result = parse_consumer_result(Path(run_result["log"])) if run_result.get("log") else None
                if run_result["status"] == "passed" and result is None:
                    run_result["status"] = "failed"
                    run_result["diagnostic"] = "consumer result marker was missing or invalid"
                if result is not None:
                    framework_record["result"] = result
                    if result.get("qnameMetadata") != qname_metadata:
                        run_result["status"] = "failed"
                        run_result["diagnostic"] = (
                            "consumer model API QName metadata does not match the packaged schema"
                        )
                    if result.get("payloadFormatVersion") != payload_format_version:
                        run_result["status"] = "failed"
                        run_result["diagnostic"] = (
                            "consumer model API payload format version does not match the packaged schema "
                            f"({result.get('payloadFormatVersion')!r} != {payload_format_version!r})"
                        )
            else:
                framework_record["run"] = {"status": "not-run", "exitCode": None, "log": None}
            receipt["consumer"].setdefault("frameworks", {})[framework] = framework_record

        framework_records = receipt["consumer"]["frameworks"].values()
        all_passed = all(
            record["compile"]["status"] == "passed" and record["run"]["status"] == "passed"
            for record in framework_records
        )
        if not all_passed:
            raise QualificationError("One or more framework compile/run gates failed or were unavailable.")

        receipt["status"] = "passed"
        print(f"Package inspection and net8.0/net10.0 package-consumer gates passed. Evidence: {evidence}")
        return 0
    except QualificationError as error:
        receipt["status"] = "failed"
        receipt["error"] = str(error)
        print(f"Qualification failed: {error}", file=sys.stderr)
        return 2
    finally:
        if receipt_path is None and evidence_created:
            try:
                evidence = ensure_evidence_outside_repo(evidence, repository)
                evidence.mkdir(parents=True, exist_ok=True)
                evidence_safe = True
                receipt_path = evidence / "receipt.json"
                receipt["evidenceDirectory"] = str(evidence)
            except QualificationError:
                receipt_path = None
        if receipt_path is not None and evidence_safe:
            receipt["receipt"] = str(receipt_path.resolve())
            receipt_path.write_text(json.dumps(receipt, indent=2, sort_keys=True) + "\n", encoding="utf-8")
            if receipt["status"] != "passed":
                print(f"Receipt: {receipt_path}", file=sys.stderr)


if __name__ == "__main__":
    raise SystemExit(main())
