import os
import time
import fnmatch
import subprocess
from pathlib import Path

# Try to use pathspec for accurate .gitignore semantics; fallback to built-in
try:
    from pathspec import PathSpec  # type: ignore
except Exception:  # pragma: no cover - optional dep
    PathSpec = None  # type: ignore

# ------------------------------------------------------------------------------
# 1) .gitignore loading and matching helpers
# ------------------------------------------------------------------------------

class _SimpleGitignoreSpec:
    """Minimal .gitignore matcher used when the pathspec package is unavailable."""

    def __init__(self, patterns):
        # patterns: list of (pat_str, is_negation, dir_only)
        self._patterns = patterns

    @classmethod
    def from_file(cls, path: Path) -> "_SimpleGitignoreSpec":
        patterns = []
        with path.open("r", encoding="utf-8", errors="ignore") as f:
            for raw in f:
                line = raw.rstrip("\r\n")
                if not line or line.startswith("#"):
                    continue
                neg = line.startswith("!")
                if neg:
                    line = line[1:]
                dir_only = line.endswith("/")
                line = line.rstrip("/").strip()
                if line:
                    patterns.append((line, neg, dir_only))
        return cls(patterns)

    def match_file(self, path: str) -> bool:
        is_dir = path.endswith("/")
        clean = path.rstrip("/")
        if not clean:
            return False
        result = False
        for pat, neg, dir_only in self._patterns:
            if dir_only and not is_dir:
                continue
            if self._pat_matches(pat, clean):
                result = not neg
        return result

    @staticmethod
    def _pat_matches(pat: str, path: str) -> bool:
        anchored = pat.startswith("/")
        pat = pat.lstrip("/")

        # ** prefix: match suffix against any trailing subpath
        if pat.startswith("**/"):
            suffix = pat[3:]
            parts = path.split("/")
            for i in range(len(parts)):
                if fnmatch.fnmatch("/".join(parts[i:]), suffix):
                    return True
            return False

        # No slash (after stripping leading /): match against any path component
        if not anchored and "/" not in pat:
            return any(fnmatch.fnmatch(p, pat) for p in path.split("/"))

        # Rooted or path-containing pattern
        if fnmatch.fnmatch(path, pat):
            return True
        # Prefix match: "foo/bar" also covers "foo/bar/baz"
        return path.startswith(pat.rstrip("/") + "/")


def load_gitignore_spec(root: Path):
    """Return a spec with a match_file() method for the .gitignore at root.

    Uses PathSpec if installed, otherwise falls back to _SimpleGitignoreSpec.
    Returns None if .gitignore does not exist.
    """
    gitignore_path = root / ".gitignore"
    if not gitignore_path.exists():
        return None
    if PathSpec:
        with gitignore_path.open("r", encoding="utf-8", errors="ignore") as f:
            return PathSpec.from_lines("gitwildmatch", f)
    return _SimpleGitignoreSpec.from_file(gitignore_path)


def get_git_included_files(root: Path):
    """Return a set of repo-relative POSIX paths that are NOT ignored by Git.

    Uses `git ls-files -co --exclude-standard`.
    Returns None (not set()) when git is unavailable or root is not a git repo.
    Returns set() only when git succeeds but the index/worktree is empty.
    """
    try:
        completed = subprocess.run(
            ["git", "ls-files", "-co", "--exclude-standard"],
            cwd=str(root),
            stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL,
            text=True,
            check=False,
        )
        if completed.returncode != 0:
            return None  # not a git repo
        files = set()
        for line in completed.stdout.splitlines():
            p = line.strip()
            if p:
                files.add(p.replace("\\", "/"))
        return files
    except Exception:
        return None  # git not installed


def build_allowed_dirs_from_files(files):
    """From a set of POSIX file paths, compute all ancestor directories."""
    allowed = {""}
    for f in files:
        parts = f.split("/")
        cur = []
        for part in parts[:-1]:  # exclude filename
            cur.append(part)
            allowed.add("/".join(cur))
    return allowed

# ------------------------------------------------------------------------------
# 2) Additional excluded extensions (non-text formats)
# ------------------------------------------------------------------------------

EXCLUDED_EXTENSIONS = {
    ".jpg", ".jpeg", ".png", ".gif", ".bmp",
    ".tiff", ".zip", ".tar", ".gz", ".rar",
    ".7z", ".pdf", ".exe", ".dll", ".bin",
    ".iso", ".db", ".cache", ".vsidx", ".suo",
    ".dtbcache", ".v2", ".futdcache",
}

# Hard exclusions applied before .gitignore/Git resolution.
HARD_EXCLUDED_ROOT_PREFIXES = {
    "workspace",
}

HARD_EXCLUDED_PATH_FRAGMENTS = {
    "logs/llm",
}

def is_text_file(file_path: Path) -> bool:
    return file_path.suffix.lower() not in EXCLUDED_EXTENSIONS


def is_hard_excluded(rel_posix_path: str) -> bool:
    normalized = str(rel_posix_path or "").strip().replace("\\", "/").strip("/")
    if not normalized:
        return False

    for prefix in HARD_EXCLUDED_ROOT_PREFIXES:
        if normalized == prefix or normalized.startswith(prefix + "/"):
            return True

    wrapped = f"/{normalized}/"
    for fragment in HARD_EXCLUDED_PATH_FRAGMENTS:
        token = "/" + fragment.strip("/") + "/"
        if wrapped == token or token in wrapped:
            return True

    return False


def _resolve_ignore_machinery(root: Path):
    """Return (spec, allowed_dirs, include_files) for the given root.

    Priority:
    - Git available: use include_files + allowed_dirs; spec = None.
    - Git unavailable: load spec from .gitignore; allowed_dirs = include_files = None.
    """
    include_files = get_git_included_files(root)
    if include_files is None:
        # Not a git repo or git unavailable — use .gitignore
        return load_gitignore_spec(root), None, None
    # Git repo
    allowed_dirs = build_allowed_dirs_from_files(include_files)
    return None, allowed_dirs, include_files


def should_ignore(rel_posix_path: str, is_dir: bool, spec, self_rel_posix: str, include_set):
    """Return True if the path should be ignored.

    Precedence:
    - Always ignore arc.py itself.
    - Hard exclusions always apply.
    - If spec is set (pathspec or built-in), use it.
    - Else if include_set is not None, ignore anything absent from it.
    """
    if rel_posix_path == self_rel_posix:
        return True

    if is_hard_excluded(rel_posix_path):
        return True

    if spec is not None:
        test = rel_posix_path
        if is_dir and not test.endswith("/"):
            test += "/"
        return bool(spec.match_file(test))

    if include_set is not None:
        return rel_posix_path not in include_set

    return False

# ------------------------------------------------------------------------------
# 3) Build directory structure as a tree string, honoring .gitignore
# ------------------------------------------------------------------------------

def get_directory_structure(directory: str) -> str:
    """Recursively build a tree string, skipping gitignored paths."""
    structure = []
    root_path = Path(directory).resolve()

    spec, allowed_dirs, include_files = _resolve_ignore_machinery(root_path)

    try:
        self_rel = Path(__file__).resolve().relative_to(root_path).as_posix()
    except Exception:
        self_rel = "arc.py"

    for current_root, dirs, files in os.walk(root_path):
        rel_dir = Path(current_root).resolve().relative_to(root_path).as_posix()
        if rel_dir == ".":
            rel_dir = ""

        # Prune ignored directories
        kept = []
        for d in dirs:
            rel = (Path(rel_dir) / d).as_posix() if rel_dir else d
            if not should_ignore(rel, True, spec, self_rel, allowed_dirs):
                kept.append(d)
        dirs[:] = kept

        # Skip this directory itself if ignored
        if rel_dir and should_ignore(rel_dir, True, spec, self_rel, allowed_dirs):
            continue

        level = rel_dir.count("/") if rel_dir else 0
        indent = "  " * level
        basename = os.path.basename(current_root) if rel_dir else os.path.basename(root_path)
        structure.append(f"{indent}- {basename}/")

        sub_indent = "  " * (level + 1)
        for file in files:
            rel_file = (Path(rel_dir) / file).as_posix() if rel_dir else file
            if should_ignore(rel_file, False, spec, self_rel, include_files):
                continue
            structure.append(f"{sub_indent}- {file}")

    return "\n".join(structure)

# ------------------------------------------------------------------------------
# 4) Main function to create the Markdown snapshot
# ------------------------------------------------------------------------------

def create_markdown_snapshot(directory: str):
    """Create a Markdown snapshot of the directory: tree + file contents."""
    timestamp = time.strftime("%Y%m%d_%H%M%S")
    dir_path = Path(directory).resolve()
    markdown_filename = f"{dir_path.name}.{timestamp}.md"
    markdown_filepath = dir_path.parent / markdown_filename

    with open(markdown_filepath, "w", encoding="utf-8") as md_file:
        # Directory structure
        md_file.write("# Directory Snapshot\n\n")
        md_file.write("```plaintext\n")
        md_file.write(get_directory_structure(str(dir_path)))
        md_file.write("\n```\n\n")

        # File contents
        spec, allowed_dirs, include_files = _resolve_ignore_machinery(dir_path)

        try:
            self_rel = Path(__file__).resolve().relative_to(dir_path).as_posix()
        except Exception:
            self_rel = "arc.py"

        for root, dirs, files in os.walk(dir_path):
            rel_root = Path(root).resolve().relative_to(dir_path).as_posix()
            if rel_root == ".":
                rel_root = ""

            # Prune ignored directories
            dirs[:] = [
                d for d in dirs
                if not should_ignore(
                    (Path(rel_root) / d).as_posix() if rel_root else d,
                    True, spec, self_rel, allowed_dirs,
                )
            ]

            if rel_root and should_ignore(rel_root, True, spec, self_rel, allowed_dirs):
                continue

            for file in files:
                file_path = Path(root) / file
                rel_path_str = file_path.resolve().relative_to(dir_path).as_posix()

                if should_ignore(rel_path_str, False, spec, self_rel, include_files):
                    continue

                if not is_text_file(file_path):
                    continue

                md_file.write(f"## `{rel_path_str}`\n\n")
                md_file.write(f"**Path:** `{rel_path_str}`\n\n")
                md_file.write("```plaintext\n")
                try:
                    with open(file_path, "r", encoding="utf-8") as f:
                        md_file.write(f.read())
                except Exception as e:
                    md_file.write(f"Error reading file: {e}")
                md_file.write("\n```\n\n")

    print(f"Markdown snapshot created: {markdown_filepath}")

# ------------------------------------------------------------------------------
# Usage
# ------------------------------------------------------------------------------
if __name__ == "__main__":
    target_directory = "."
    create_markdown_snapshot(target_directory)
