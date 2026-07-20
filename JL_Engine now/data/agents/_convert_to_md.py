"""Generic JSON → Markdown converter for JL Engine agent configs.

Preserves every field one-to-one. Handles:
- Nested dicts as headings
- Arrays of strings as bullet lists
- Arrays of homogeneous dicts as markdown tables
- Multi-line strings as fenced code blocks
- Scalar values inline
"""
import json
import re
from pathlib import Path

AGENTS_DIR = Path(__file__).parent

def title_case(key: str) -> str:
    """ConvertSnake_case or camelCase to Title Case."""
    s = key.replace('_', ' ').replace('-', ' ')
    s = re.sub(r'([a-z])([A-Z])', r'\1 \2', s)
    return ' '.join(w.capitalize() for w in s.split())

def fmt_scalar(v) -> str:
    if v is None:
        return "*(null)*"
    if isinstance(v, bool):
        return f"`{str(v).lower()}`"
    if isinstance(v, (int, float)):
        return str(v)
    if isinstance(v, str):
        if '\n' in v:
            return f"\n```\n{v}\n```\n"
        return v
    return str(v)

def is_simple_scalar(v) -> bool:
    return v is None or isinstance(v, (bool, int, float)) or (isinstance(v, str) and '\n' not in v and len(v) < 200)

def is_homogeneous_dict_list(arr) -> bool:
    """All items are dicts and all values are simple (no nested dicts/lists with content)."""
    if not arr or not all(isinstance(x, dict) for x in arr):
        return False
    for x in arr:
        for v in x.values():
            if isinstance(v, dict) and v:
                return False
            if isinstance(v, list):
                # allow lists of strings (we'll join), reject lists of dicts
                if any(isinstance(item, dict) for item in v):
                    return False
    return True

def render_table(arr) -> str:
    """Render homogeneous list of dicts as a markdown table."""
    keys = []
    for x in arr:
        for k in x.keys():
            if k not in keys:
                keys.append(k)
    lines = ["| " + " | ".join(keys) + " |", "|" + "|".join(["---"] * len(keys)) + "|"]
    for x in arr:
        row = []
        for k in keys:
            v = x.get(k, "")
            if isinstance(v, list):
                v = ", ".join(str(item) for item in v)
            elif isinstance(v, bool):
                v = str(v).lower()
            elif v is None:
                v = ""
            else:
                v = str(v).replace("|", "\\|").replace("\n", " ")
            row.append(v)
        lines.append("| " + " | ".join(row) + " |")
    return "\n".join(lines)

def render_value(value, depth: int) -> str:
    """Render a JSON value at the given heading depth (max 6)."""
    depth = min(depth, 6)

    if isinstance(value, dict):
        return render_dict(value, depth)
    if isinstance(value, list):
        if not value:
            return "*(empty)*"
        if all(isinstance(x, str) for x in value):
            return "\n".join(f"- {x}" for x in value)
        if is_homogeneous_dict_list(value):
            return render_table(value)
        # Heterogeneous list — render each item with a divider
        out = []
        for i, item in enumerate(value):
            out.append(f"**[{i}]**\n")
            out.append(render_value(item, depth + 1))
            out.append("")
        return "\n".join(out)
    return fmt_scalar(value)

def render_dict(d: dict, depth: int) -> str:
    """Render a dict. Scalars inline as bullets, complex values as subheadings."""
    if not d:
        return "*(empty)*"

    # Separate simple from complex
    simple = []
    complex_items = []
    for k, v in d.items():
        if is_simple_scalar(v):
            simple.append((k, v))
        elif isinstance(v, list) and all(isinstance(x, str) for x in v):
            simple.append((k, v))  # we'll render bullet list inline
        else:
            complex_items.append((k, v))

    out = []
    if simple:
        for k, v in simple:
            if isinstance(v, list):
                out.append(f"- **{k}**:")
                for item in v:
                    out.append(f"  - {item}")
            else:
                out.append(f"- **{k}**: {fmt_scalar(v)}")
        if complex_items:
            out.append("")

    for k, v in complex_items:
        heading = "#" * depth + " " + title_case(k)
        out.append("")
        out.append(heading)
        out.append("")
        # Render scalar-only dicts as compact bullet list, but multiline strings as code blocks
        if isinstance(v, str) and '\n' in v:
            out.append(f"```\n{v}\n```")
        else:
            out.append(render_value(v, depth + 1))

    return "\n".join(out)

def convert_agent(json_path: Path) -> str:
    data = json.loads(json_path.read_text(encoding='utf-8'))

    # Title from identity.name if present, else filename
    name = data.get('identity', {}).get('name') if isinstance(data.get('identity'), dict) else None
    if not name:
        name = json_path.stem.replace('_Full', '').replace('_', ' ')

    out = [f"# {name}", ""]

    # License header (if present) — emit as blockquote
    if '_license' in data:
        out.append(f"> _license_: {data['_license']}")
    if '_protected' in data:
        out.append(f">")
        out.append(f"> _protected_: `{str(data['_protected']).lower()}`")
    if '_license' in data or '_protected' in data:
        out.append("")
        out.append("---")
        out.append("")

    # Render top-level keys (skipping _license and _protected which we handled)
    body_dict = {k: v for k, v in data.items() if not k.startswith('_')}
    out.append(render_dict(body_dict, depth=2))

    return "\n".join(out) + "\n"

def main():
    converted = []
    skipped = []
    for json_file in sorted(AGENTS_DIR.glob("*.json")):
        md_file = json_file.with_suffix('.md')
        try:
            md = convert_agent(json_file)
            md_file.write_text(md, encoding='utf-8')
            converted.append((json_file.name, md_file.name, len(md)))
        except Exception as e:
            skipped.append((json_file.name, str(e)))

    import sys
    sys.stdout.reconfigure(encoding='utf-8')
    print("\n=== CONVERTED ===")
    for src, dst, sz in converted:
        print(f"  {src:40s} -> {dst:40s} ({sz:,} bytes)")
    if skipped:
        print("\n=== SKIPPED ===")
        for src, err in skipped:
            print(f"  {src}: {err}")
    print(f"\nTotal: {len(converted)} converted, {len(skipped)} skipped")

if __name__ == "__main__":
    main()
