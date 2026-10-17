#!/usr/bin/env python3
"""Lint the GitHub workflows: strict YAML (duplicate keys are errors) plus reuse-wiring invariants.

Duplicate keys are the failure mode this guards against: a step can silently lose its `if:` guard
(or a `with:`) because YAML keeps the last duplicate, which turns a cheap reuse into a full rebuild
or, worse, drops a condition entirely.
"""
from __future__ import annotations

import pathlib
import sys

import yaml

WORKFLOWS = pathlib.Path(__file__).resolve().parents[1] / ".github" / "workflows"


class StrictLoader(yaml.SafeLoader):
    pass


def _mapping(loader: StrictLoader, node: yaml.MappingNode, deep: bool = False) -> dict:
    result: dict = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node, deep=deep)
        if key in result:
            raise yaml.constructor.ConstructorError(
                None, None, f"duplicate key {key!r}", key_node.start_mark)
        result[key] = loader.construct_object(value_node, deep=deep)
    return result


StrictLoader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, _mapping)


def build_steps(document: dict) -> list[dict]:
    jobs = document.get("jobs") or {}
    build = jobs.get("build") or {}
    return build.get("steps") or []


def check_build(document: dict, name: str) -> list[str]:
    problems: list[str] = []
    steps = build_steps(document)
    if not steps:
        return [f"{name}: no build steps found"]
    guarded = "steps.reuse.outputs.skip != 'true'"
    by_name = {step.get("name"): step for step in steps if isinstance(step, dict)}
    # Heavy steps must stay behind the reuse guard, otherwise a repeated release rebuilds everything.
    for step_name in ("Publish Native AOT", "Restore and test", "Archive build"):
        step = by_name.get(step_name)
        if step is None:
            problems.append(f"{name}: missing step {step_name!r}")
        elif guarded not in str(step.get("if", "")):
            problems.append(f"{name}: {step_name!r} lost its reuse guard")
    probe = by_name.get("Look for a reusable build")
    if probe is None or not str(probe.get("run", "")).strip():
        problems.append(f"{name}: reuse probe step is missing")
    # Metadata must always publish, even when the build was skipped, or packaging cannot find the binaries.
    finalize = by_name.get("Finalize build metadata")
    if finalize is None or finalize.get("if") != "always()":
        problems.append(f"{name}: build metadata is not published unconditionally")
    return problems


def job_steps(document: dict, job: str) -> list[dict]:
    return (document.get("jobs") or {}).get(job, {}).get("steps") or []


def check_release(document: dict, name: str) -> list[str]:
    """The release gate must verify package bytes without reintroducing Java payloads."""
    problems: list[str] = []
    steps = job_steps(document, "preflight") + job_steps(document, "release")
    runs = [str(step.get("run", "")) for step in steps if isinstance(step, dict)]
    evidence_calls = [run for run in runs if "release_evidence.py" in run]
    if not evidence_calls:
        problems.append(f"{name}: no job verifies downloaded package bytes")
    for run in evidence_calls:
        for forbidden in ("--sources", "--receipts", "source-closure", "release_preflight.py"):
            if forbidden in run:
                problems.append(f"{name}: Java/source gate {forbidden!r} remains in release evidence")
    return problems


def triggers(document: dict) -> dict:
    """The `on:` block. PyYAML resolves the bare key `on` to the boolean True, so accept both."""
    for key in ("on", True):
        value = document.get(key)
        if isinstance(value, dict):
            return value
    return {}


def check_native_acceptance(document: dict, name: str) -> list[str]:
    """The manual six-RID acceptance entry must reuse build.yml and must not publish."""
    problems: list[str] = []
    jobs = document.get("jobs") or {}
    build = jobs.get("build") or {}
    if build.get("uses") != "./.github/workflows/build.yml":
        problems.append(f"{name}: acceptance must reuse build.yml, not duplicate its steps")
    if (build.get("with") or {}).get("platforms") != "all":
        problems.append(f"{name}: acceptance must cover all six RIDs")
    if "release" in jobs or "package" in jobs:
        problems.append(f"{name}: acceptance entry must not package or release")
    dispatch = triggers(document).get("workflow_dispatch")
    if not isinstance(dispatch, dict):
        problems.append(f"{name}: acceptance must be dispatched manually (workflow_dispatch)")
        return problems
    reuse = (dispatch.get("inputs") or {}).get("reuse") or {}
    if reuse.get("default") is not False:
        problems.append(f"{name}: reuse must default to false so a dispatch runs a real rebuild")
    return problems


def main() -> int:
    problems: list[str] = []
    documents = {}
    for path in sorted(WORKFLOWS.glob("*.yml")):
        try:
            documents[path.name] = yaml.load(path.read_text(), Loader=StrictLoader)
            print(f"yaml ok: {path.name}")
        except yaml.YAMLError as error:
            problems.append(f"{path.name}: {error}")
    if "build.yml" in documents:
        problems += check_build(documents["build.yml"], "build.yml")
    if "release.yml" in documents:
        problems += check_release(documents["release.yml"], "release.yml")
    if "native-acceptance.yml" in documents:
        problems += check_native_acceptance(documents["native-acceptance.yml"], "native-acceptance.yml")
    for problem in problems:
        print(f"ERROR {problem}", file=sys.stderr)
    return 1 if problems else 0


if __name__ == "__main__":
    raise SystemExit(main())
