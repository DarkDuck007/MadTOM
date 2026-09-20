#!/usr/bin/env python3
"""
doc_tool.py - Documentation helper script for MADTOM.
Validates relative markdown links and anchors across README.md and Documentation/*.md,
and provides documentation summaries.
"""

import sys
import os
import re
import argparse
from pathlib import Path
from urllib.parse import urlparse, unquote

def gfm_slugify(text):
    """
    Generate GitHub Flavored Markdown (GFM) anchor slug:
    - Downcase text
    - Strip leading/trailing whitespace
    - Remove punctuation characters (keeping alphanumeric, spaces, hyphens, underscores)
    - Replace every space with a hyphen '-' (preserving multiple spaces as multiple hyphens)
    """
    text = text.lower().strip()
    cleaned = []
    for ch in text:
        if ch.isalnum() or ch in (' ', '-', '_'):
            cleaned.append(ch)
    # Replace space with hyphen
    return ''.join(cleaned).replace(' ', '-')

def extract_headings(file_path):
    """Extract all headings from a markdown file and compute both GFM and relaxed slugs."""
    slugs = set()
    try:
        with open(file_path, "r", encoding="utf-8") as f:
            for line in f:
                match = re.match(r'^(#{1,6})\s+(.+)$', line.strip())
                if match:
                    heading_text = match.group(2).strip()
                    # Remove trailing '#' if present
                    heading_text = re.sub(r'\s+#+$', '', heading_text)
                    slugs.add(gfm_slugify(heading_text))
                    # Also add a single-hyphen collapsed variant for robustness
                    slugs.add(re.sub(r'-+', '-', gfm_slugify(heading_text)))
    except Exception as e:
        print(f"Warning: could not read headings from {file_path}: {e}")
    return slugs

def check_markdown_links(root_dir):
    """Check all relative markdown links in README.md and Documentation/*.md."""
    root = Path(root_dir).resolve()
    doc_files = []
    
    readme = root / "README.md"
    if readme.exists():
        doc_files.append(readme)
        
    doc_dir = root / "Documentation"
    if doc_dir.exists():
        for p in sorted(doc_dir.rglob("*.md")):
            doc_files.append(p)

    link_pattern = re.compile(r'\[([^\]]+)\]\(([^)]+)\)')
    errors = []
    total_links = 0
    checked_links = 0

    file_headings = {}
    for doc in doc_files:
        file_headings[doc.resolve()] = extract_headings(doc)

    for doc in doc_files:
        try:
            with open(doc, "r", encoding="utf-8") as f:
                lines = f.readlines()
        except Exception as e:
            errors.append(f"Failed to read {doc}: {e}")
            continue

        for line_num, line in enumerate(lines, start=1):
            for match in link_pattern.finditer(line):
                total_links += 1
                link_text, link_target = match.group(1), match.group(2).strip()
                
                # Strip title if present, e.g. [text](path "title")
                link_target = link_target.split()[0]

                # Ignore external URLs, mailto, etc.
                if re.match(r'^(https?://|mailto:|ftp:)', link_target):
                    continue

                checked_links += 1
                
                # Handle file:// URIs
                if link_target.startswith("file://"):
                    parsed = urlparse(link_target)
                    file_path = Path(unquote(parsed.path))
                    anchor = parsed.fragment if parsed.fragment else None
                    target_path = file_path
                else:
                    # Split path and anchor
                    if '#' in link_target:
                        target_path_str, anchor = link_target.split('#', 1)
                    else:
                        target_path_str, anchor = link_target, None

                    if target_path_str:
                        target_path = (doc.parent / target_path_str).resolve()
                    else:
                        target_path = doc.resolve()

                if not target_path.exists():
                    errors.append(
                        f"{doc.relative_to(root)}:{line_num} -> Broken link to missing file: '{link_target}'"
                    )
                    continue

                if anchor and target_path.suffix.lower() == '.md':
                    headings = file_headings.get(target_path)
                    if headings is None:
                        headings = extract_headings(target_path)
                        file_headings[target_path] = headings

                    slug = gfm_slugify(anchor)
                    if slug not in headings and anchor not in headings and re.sub(r'-+', '-', slug) not in headings:
                        errors.append(
                            f"{doc.relative_to(root)}:{line_num} -> Broken anchor '#{anchor}' in '{target_path.relative_to(root)}'"
                        )

    return total_links, checked_links, errors

def cmd_check_links(args):
    root_dir = args.root or os.getcwd()
    total, checked, errors = check_markdown_links(root_dir)
    print(f"Scanned markdown files in: {root_dir}")
    print(f"Total links found: {total}, Internal links verified: {checked}")
    
    if errors:
        print(f"\n❌ Found {len(errors)} broken link(s):")
        for err in errors:
            print(f"  - {err}")
        sys.exit(1)
    else:
        print("\n✅ All internal documentation links and anchors are valid!")
        sys.exit(0)

def cmd_summary(args):
    root_dir = args.root or os.getcwd()
    root = Path(root_dir).resolve()
    doc_files = []
    
    readme = root / "README.md"
    if readme.exists():
        doc_files.append(readme)
        
    doc_dir = root / "Documentation"
    if doc_dir.exists():
        for p in sorted(doc_dir.rglob("*.md")):
            doc_files.append(p)

    print(f"Documentation Overview for {root.name}:")
    print("=" * 60)
    for doc in doc_files:
        try:
            content = doc.read_text(encoding="utf-8")
            words = len(content.split())
            lines = len(content.splitlines())
            headings = [line.strip() for line in content.splitlines() if line.startswith("#")]
            rel_path = doc.relative_to(root)
            print(f"\n📄 {rel_path} ({lines} lines, {words} words)")
            for h in headings[:6]:
                print(f"   {h}")
            if len(headings) > 6:
                print(f"   ... and {len(headings) - 6} more sections")
        except Exception as e:
            print(f"Error reading {doc}: {e}")

def main():
    parser = argparse.ArgumentParser(description="MADTOM Documentation Sync & Verification Tool")
    parser.add_argument("--root", default=None, help="Root directory of the project (default: current directory)")
    subparsers = parser.add_subparsers(dest="command", required=True)

    p_check = subparsers.add_parser("check-links", help="Verify all relative markdown links and anchors")
    p_check.set_defaults(func=cmd_check_links)

    p_summary = subparsers.add_parser("summary", help="Summarize documentation files and structure")
    p_summary.set_defaults(func=cmd_summary)

    args = parser.parse_args()
    args.func(args)

if __name__ == "__main__":
    main()

