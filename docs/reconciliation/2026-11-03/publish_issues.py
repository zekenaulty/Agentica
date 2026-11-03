"""Publish this reconciled plan idempotently; writes only this Agentica workspace/GitHub repo."""
import argparse
import json
from pathlib import Path
import re
import subprocess

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[2]
REPO = "zekenaulty/Agentica"
PLAN = json.loads((HERE / "planning-candidates.json").read_text(encoding="utf-8-sig"))
BODIES = ROOT / ".agentica" / "reconciliation" / "issue-bodies"
STATE = HERE / "published-issues.json"


def gh(*args):
    result = subprocess.run(["gh", *args], cwd=ROOT, text=True, encoding="utf-8", capture_output=True, timeout=120)
    if result.returncode:
        raise RuntimeError(result.stderr.strip() or result.stdout.strip())
    return result.stdout.strip()


def marker(key):
    return f"<!-- agentica-plan:{PLAN['date']}:{key} -->"


def bullets(items):
    return "\n".join(f"- {item}" for item in items)


def normalized_body(body):
    return body.replace("\r\n", "\n").replace("\r", "\n").strip()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--publish", action="store_true", help="Explicitly create/update issues and plan labels")
    args = parser.parse_args()
    leaves = [item for item in PLAN["candidates"] if item["publication_action"] == "create"]
    leaves += [
        dict(id="Q-07", epic="OA-Q", title="Consolidate the durable host operation and action-recovery branch stack", priority=0,
             outcome="Review and integrate the existing 26-commit Agentica durable operation/start/adoption/recovery frontier without losing local work or touching Maze Battle.",
             dependencies=[], paths=["Agentica.Lab.Web/Runtime/", "Agentica.Lab.Web/Contracts/", "Agentica.Lab.Web/wwwroot/sdk/", "Agentica.Lab.Web.Tests/", "browser-tests/"],
             acceptance=["Pin and independently review codex/maze-action-recovery27cb2ce and all superseded branch dispositions.", "Reconcile with merged Lab chat, preserve original effect/start identity, restart custody, cumulative limits, late recovery and bounded wake replay.", "Qualify actual HTTP/socket/SQLite and browser SDK regressions, required CI and exact-head review before main admission.", "Preserve ignored live state and every unrelated checkout; record retirement rather than deleting unchecked work."],
             boundaries=["Agentica-side only; no Maze Battle, Bridge, Cloud or Nyx configuration, code, data or runtime changes."], source_keys=["epic-plan", "long-horizon"]),
        dict(id="Q-08", epic="OA-Q", title="Keep retained trace inspection readable while the live event tail is full", priority=0,
             outcome="Resolve the outstanding PR11 review: fetched older trace pages must remain visible despite a full later live tail and continuing SSE.",
             dependencies=[], paths=["Agentica.Lab.Web/wwwroot/chat.mjs", "Agentica.Lab.Web.Tests/chat-ui.test.mjs", "browser-tests/ui-experiment-retained-trace.mjs"],
             acceptance=["Reproduce live1001–2000 plus retained1–100; the inspected page is visible and its cursor remains honest.", "Continuing SSE does not evict the chosen retained page; additional retained pages and original payloads remain inspectable.", "Resume live shows the bounded current tail, preserves explicit disclosure state and reports omissions accurately.", "Browser and JavaScript regressions pass; preserve canonical messages, source offsets, provider requests and persisted trace data."],
             boundaries=["Presentation correction only; no provider request, chat migration or trace deletion."], source_keys=[]),
    ]
    keys = [item["id"] for item in PLAN["epics"]] + [item["id"] for item in leaves]
    assert len(keys) == len(set(keys)) and all(item["epic"] in keys for item in leaves)
    if not args.publish:
        print(json.dumps({"program": 1, "epics": len(PLAN["epics"]), "new_leaves": len(leaves), "existing_dispositions": 4, "effects": "none"}))
        return
    BODIES.mkdir(parents=True, exist_ok=True)
    existing = json.loads(gh("issue", "list", "--repo", REPO, "--state", "all", "--limit", "1000", "--json", "number,title,body,url,state"))
    labels = {item["name"] for item in json.loads(gh("label", "list", "--repo", REPO, "--limit", "100", "--json", "name"))}
    for name, color, description in [
        ("type:program", "5319e7", "Reconciled multi-epic Agentica program"), ("type:epic", "7057ff", "Bounded group of linked outcomes"),
        ("priority:p0", "b60205", "First implementation/consolidation wave"), ("priority:p1", "d93f0b", "Next qualified wave"),
        ("priority:p2", "fbca04", "Dependency-led follow-up"), ("priority:p3", "c2e0c6", "Deferred comparison/research"),
    ]:
        if name not in labels:
            gh("label", "create", name, "--repo", REPO, "--color", color, "--description", description)
    state = {"date": PLAN["date"], "repository": REPO, "source_main": PLAN["source_pins"]["main"], "issues": {}}
    links = {item["id"]: item["issue_url"] for item in PLAN["candidates"] if item.get("issue_url")}
    expandable_skeletons = {}

    def publish(key, title, body, issue_labels, update=False):
        matches = [item for item in existing if marker(key) in item.get("body", "")]
        if len(matches) > 1:
            raise RuntimeError(f"Duplicate publication markers: {key}")
        path = BODIES / f"{key}.md"
        generated_body = marker(key) + "\n\n" + body + "\n"
        path.write_text(generated_body, encoding="utf-8")
        if matches:
            item = matches[0]
            # First-phase exact recognition recovers interrupted publication without
            # treating an expanded, edited or closed workflow body as a skeleton.
            if not update and item["state"].upper() == "OPEN" and normalized_body(item["body"]) == normalized_body(generated_body):
                expandable_skeletons[key] = normalized_body(generated_body)
            if update and key in expandable_skeletons:
                # A human may have edited or closed it since the initial listing.
                current = json.loads(gh("issue", "view", str(item["number"]), "--repo", REPO, "--json", "body,state"))
                item.update(current)
                if item["state"].upper() == "OPEN" and normalized_body(item["body"]) == expandable_skeletons[key]:
                    gh("issue", "edit", str(item["number"]), "--repo", REPO, "--body-file", str(path))
                    item["body"] = generated_body
                del expandable_skeletons[key]
        else:
            url = gh("issue", "create", "--repo", REPO, "--title", title, "--body-file", str(path), "--label", ",".join(issue_labels))
            match = re.fullmatch(r"https://github\.com/zekenaulty/Agentica/issues/(\d+)", url)
            if not match:
                raise RuntimeError("Unknown issue-create outcome; rerun to reconcile the publication marker")
            item = dict(number=int(match[1]), url=url, title=title, body=generated_body, state="OPEN")
            existing.append(item)
            if not update:
                expandable_skeletons[key] = normalized_body(generated_body)
        links[key] = item["url"]
        state["issues"][key] = {name: item[name] for name in ["number", "url", "title", "state"]}
        STATE.write_text(json.dumps(state, indent=2) + "\n", encoding="utf-8")
        print(f"{key}: {item['url']}", flush=True)

    program_key = PLAN["program"]["id"]
    scope = "All implementation and local writes remain in Agentica and its own workspaces. Bridge/Cloud/Nyx/Maze ownership, data and routes are external/deferred. The owner authorizes issue publication, bounded implementation and stable reviewed merges on November3; earlier publication-only limits describe their original planning event."
    sources = "\n".join(f"- [{item['title']}]({item['url']}) — modified `{item['modified_time']}`" for item in PLAN["sources"])
    base_program = f"## Outcome\n\n{PLAN['program']['title']}\n\n{scope}\n\n## Reconciled source direction\n\n{sources}\n\n## Completion\n\n{PLAN['program']['acceptance']} Separate execution, evidence, fulfillment and effect custody. A merged fixture is not live adoption. No percentage savings, current Nyx migration or provider parity is claimed without corresponding evidence.\n"
    publish(program_key, "[Program] " + PLAN["program"]["title"], base_program, ["type:program", "enhancement"])
    for epic in PLAN["epics"]:
        body = f"Parent storyboard: {links[program_key]}\n\n## Outcome\n\n{epic['outcome']}\n\n## Closure\n\n{epic['closure']}\n\n{scope}"
        publish(epic["id"], f"[Epic {epic['id']}] {epic['title']}", body, ["type:epic", "enhancement"])
    for leaf in leaves:
        publish(leaf["id"], f"[{leaf['id']}] {leaf['title']}", leaf["outcome"], ["bug" if leaf["id"] in ["H-01", "Q-08"] else "enhancement", f"priority:p{leaf['priority']}"])
    for leaf in leaves:
        deps = [links.get(key, key) for key in leaf["dependencies"]]
        refs = [item for item in PLAN["sources"] if item["key"] in leaf.get("source_keys", [])]
        body = f"Parent: {links[leaf['epic']]} | Storyboard: {links[program_key]}\n\n## Outcome\n\n{leaf['outcome']}\n\n## Source and current gap\n\nInspected main `{PLAN['source_pins']['main']}`; current code/tests outrank historical prose.\n\n{leaf.get('current_gap', 'Bounded outcome from the reconciled plan or current PR review; inspect current source before implementation.')}\n\n" + bullets([f"[{item['title']}]({item['url']}) — `{item['modified_time']}`" for item in refs])
        body += "\n\n## Dependencies\n\n" + (bullets(deps) if deps else "None. This leaf may proceed independently.")
        body += "\n\n## Scope\n\n" + bullets(f"`{path}`" for path in leaf["paths"])
        body += "\n\n## Acceptance\n\n" + "\n".join(f"- [ ] {criterion}" for criterion in leaf["acceptance"])
        body += "\n\n## Boundaries\n\n" + bullets(leaf["boundaries"]) + "\n\n" + scope
        body += f"\n\n## Migration, rollback and handoff\n\n{leaf.get('migration', 'No production-data migration.')} {leaf.get('rollback', 'Revert the bounded change while preserving original effects, evidence and custody.')}\n\nReport exact source/contract/fixture cuts, selected commands/results, PASS/FAIL/UNKNOWN/SKIP/NOT-RUN per claimed boundary, unresolved original attempts and the next usable handoff.\n\n## Workflow\n\nUnclaimed at publication. Use one isolated workspace per implementation claim; the coordinator owns integration and schema/file leases. Offline proof does not authorize paid experiments or external-host changes.\n"
        publish(leaf["id"], f"[{leaf['id']}] {leaf['title']}", body, [], update=True)
    for epic in PLAN["epics"]:
        children = [item for item in PLAN["candidates"] if item["epic"] == epic["id"]] + [item for item in leaves if item["id"] in ["Q-07", "Q-08"] and item["epic"] == epic["id"]]
        tasks = "\n".join(f"- [{'x' if item.get('publication_action') == 'record-existing' else ' '}] {item['id']}: {links.get(item['id'], 'implemented source; see reconciliation manifest')} — {item['title']}" for item in children)
        publish(epic["id"], epic["title"], f"Parent storyboard: {links[program_key]}\n\n## Outcome\n\n{epic['outcome']}\n\n## Children\n\n{tasks}\n\n## Closure\n\n{epic['closure']}\n\n{scope}", [], update=True)
    publish(program_key, PLAN["program"]["title"], base_program + "\n## Epics\n\n" + bullets(f"[ ] {links[item['id']]} — {item['title']}" for item in PLAN["epics"]) + "\n\n## Existing work and custody\n\n- Existing provider-default audit remains #10.\n- Existing Lab chat remains PR #11; Q-08 resolves its outstanding review.\n- PRs #5–9 and semantic projection remain linked completed substrate.\n- Q-07 owns the unmerged durable-operation frontier; Q-03 harvests twelve local guidance/status files.\n- See the tracked reconciliation/custody manifest for every local/remote ref and preserved ignored runtime state.\n", [], update=True)


if __name__ == "__main__":
    main()
