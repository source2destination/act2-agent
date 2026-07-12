#!/usr/bin/env python3
"""Source-only audit for environments without the .NET SDK or Docker."""
from __future__ import annotations

import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
PROJECT = ROOT / "act2-harness.csproj"


def scan_balanced(text: str) -> tuple[bool, str]:
    stack: list[tuple[str, int]] = []
    pairs = {"}": "{", ")": "(", "]": "["}
    i = 0
    line = 1
    while i < len(text):
        c = text[i]
        nxt = text[i + 1] if i + 1 < len(text) else ""
        if c == "\n":
            line += 1
            i += 1
            continue
        if c == "/" and nxt == "/":
            i = text.find("\n", i + 2)
            if i < 0:
                return (not stack, "")
            continue
        if c == "/" and nxt == "*":
            end = text.find("*/", i + 2)
            if end < 0:
                return False, f"unclosed comment at line {line}"
            line += text[i : end + 2].count("\n")
            i = end + 2
            continue

        raw_start = i
        while raw_start < len(text) and text[raw_start] == "$":
            raw_start += 1
        if text.startswith('"""', raw_start):
            q = 3
            while raw_start + q < len(text) and text[raw_start + q] == '"':
                q += 1
            end = text.find('"' * q, raw_start + q)
            if end < 0:
                return False, f"unclosed raw string at line {line}"
            line += text[i : end + q].count("\n")
            i = end + q
            continue

        prefix = next((p for p in ('$@"', '@$"', '@"') if text.startswith(p, i)), None)
        if prefix:
            i += len(prefix)
            while i < len(text):
                if text[i] == '"':
                    if i + 1 < len(text) and text[i + 1] == '"':
                        i += 2
                        continue
                    i += 1
                    break
                if text[i] == "\n":
                    line += 1
                i += 1
            else:
                return False, f"unclosed verbatim string at line {line}"
            continue

        if c == '"' or (c == "$" and nxt == '"'):
            i += 2 if c == "$" else 1
            while i < len(text):
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == '"':
                    i += 1
                    break
                if text[i] == "\n":
                    line += 1
                i += 1
            else:
                return False, f"unclosed string at line {line}"
            continue

        if c == "'":
            i += 1
            while i < len(text):
                if text[i] == "\\":
                    i += 2
                    continue
                if text[i] == "'":
                    i += 1
                    break
                i += 1
            else:
                return False, f"unclosed char at line {line}"
            continue

        if c in "{([":
            stack.append((c, line))
        elif c in "})]":
            if not stack or stack[-1][0] != pairs[c]:
                return False, f"unexpected {c} at line {line}"
            stack.pop()
        i += 1

    if stack:
        return False, f"unclosed {stack[-1][0]} from line {stack[-1][1]}"
    return True, ""


def main() -> int:
    errors: list[str] = []
    project_text = PROJECT.read_text(encoding="utf-8")
    compile_files = re.findall(r'<Compile Include="([^"]+)"', project_text)
    print(f"compile boundary: {len(compile_files)} files")
    for rel in compile_files:
        path = ROOT / rel
        if not path.exists():
            errors.append(f"missing compile source: {rel}")
            continue
        ok, why = scan_balanced(path.read_text(encoding="utf-8"))
        print(f"{'PASS' if ok else 'FAIL'} balance {rel}{': ' + why if why else ''}")
        if not ok:
            errors.append(f"{rel}: {why}")

    zero = (ROOT / "zero" / "Dockerfile.zero").read_text(encoding="utf-8")
    for rel in compile_files:
        name = Path(rel).name
        if name not in zero:
            errors.append(f"zero Dockerfile does not COPY {name}")
    checks = {
        "remote lanes empty": 'ENV REMOTE_LANES=""' in zero,
        "Peak local fallback": '"--local-fallback"' in zero,
        "format compliance": '"--comply"' in zero,
        "lean remote prompts": '"--lean-prompts"' in zero,
        "lossless normalization": '"--normalize-input"' in zero,
        "Python AST runtime": "python3-minimal" in zero,
        "self-test entry": "--self-test" in (ROOT / "src" / "HarnessProgram.cs").read_text(encoding="utf-8"),
    }
    for name, passed in checks.items():
        print(f"{'PASS' if passed else 'FAIL'} {name}")
        if not passed:
            errors.append(name)

    secret_patterns = [r"sk-[A-Za-z0-9]{20,}", r"FIREWORKS_API_KEY\s*=\s*['\"][^$]"]
    for path in [ROOT / rel for rel in compile_files]:
        text = path.read_text(encoding="utf-8")
        for pattern in secret_patterns:
            if re.search(pattern, text):
                errors.append(f"possible embedded secret in {path.relative_to(ROOT)}")

    if errors:
        print("\nAUDIT FAIL")
        for error in errors:
            print("-", error)
        return 1
    print("\nAUDIT PASS")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
