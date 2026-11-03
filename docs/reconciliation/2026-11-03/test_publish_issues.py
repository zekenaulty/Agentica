"""Offline publisher recovery proof; all generated files stay under .agentica/verification."""
import contextlib
import copy
import importlib.util
import io
import json
from pathlib import Path
import re
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
SPEC = importlib.util.spec_from_file_location("issue_publisher_under_test", HERE / "publish_issues.py")
PUBLISHER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PUBLISHER)


class FakeGitHub:
    """Persist fake server state across publisher invocations without executing gh."""

    def __init__(self):
        self.issues = []
        self.initial_bodies = {}
        self.edits = []
        self.fail_create = None
        self.commit_before_failure = False
        self.fail_edit = None
        self.on_view = None

    @staticmethod
    def key(body):
        return re.search(r"<!-- agentica-plan:[^:]+:([^ ]+) -->", body).group(1)

    def issue(self, key):
        return next(item for item in self.issues if self.key(item["body"]) == key)

    def __call__(self, *args):
        if args[:2] == ("label", "list"):
            return json.dumps([{ "name": name } for name in
                ["type:program", "type:epic", "priority:p0", "priority:p1", "priority:p2", "priority:p3"]])
        if args[:2] == ("issue", "list"):
            return json.dumps(self.issues)
        if args[:2] == ("issue", "create"):
            body = Path(args[args.index("--body-file") + 1]).read_text(encoding="utf-8")
            key = self.key(body)
            failed = key == self.fail_create
            if failed and not self.commit_before_failure:
                self.fail_create = None
                raise RuntimeError("injected create failure")
            if any(self.key(item["body"]) == key for item in self.issues):
                raise AssertionError("duplicate issue creation: " + key)
            number = len(self.issues) + 100
            item = dict(number=number, title=args[args.index("--title") + 1], body=body,
                url=f"https://github.com/zekenaulty/Agentica/issues/{number}", state="OPEN")
            self.issues.append(item)
            self.initial_bodies[key] = body
            if failed:
                self.fail_create = None
                raise RuntimeError("injected lost create acknowledgement")
            return item["url"]
        if args[:2] in [("issue", "view"), ("issue", "edit")]:
            item = next(item for item in self.issues if item["number"] == int(args[2]))
            key = self.key(item["body"])
            if args[1] == "view":
                if self.on_view:
                    self.on_view(item, key)
                return json.dumps({name: item[name] for name in ["body", "state"]})
            if key == self.fail_edit:
                self.fail_edit = None
                raise RuntimeError("injected expansion failure")
            item["body"] = Path(args[args.index("--body-file") + 1]).read_text(encoding="utf-8")
            self.edits.append(key)
            return ""
        raise AssertionError("Unexpected gh operation: " + repr(args))


class PublisherRecoveryTests(unittest.TestCase):
    def setUp(self):
        verification = PUBLISHER.ROOT / ".agentica" / "verification"
        verification.mkdir(parents=True, exist_ok=True)
        self.scratch = tempfile.TemporaryDirectory(prefix="publisher-recovery-", dir=verification)
        self.addCleanup(self.scratch.cleanup)
        root = Path(self.scratch.name)
        self.bodies = root / "bodies"
        self.state = root / "published-issues.json"
        self.github = FakeGitHub()

    def publish(self):
        with patch.object(PUBLISHER, "gh", self.github), \
             patch.object(PUBLISHER, "BODIES", self.bodies), \
             patch.object(PUBLISHER, "STATE", self.state), \
             patch.object(sys, "argv", ["publish_issues.py", "--publish"]), \
             contextlib.redirect_stdout(io.StringIO()):
            PUBLISHER.main()

    def assert_fully_expanded(self):
        program = PUBLISHER.PLAN["program"]["id"]
        epics = {item["id"] for item in PUBLISHER.PLAN["epics"]}
        leaves = {item["id"] for item in PUBLISHER.PLAN["candidates"]
            if item["publication_action"] == "create"} | {"Q-07", "Q-08"}
        expected = {program} | epics | leaves
        self.assertEqual(expected, {self.github.key(item["body"]) for item in self.github.issues})
        self.assertEqual(len(expected), len(self.github.issues))
        for key in leaves:
            body = self.github.issue(key)["body"]
            for section in ["## Acceptance", "## Dependencies", "## Boundaries", "## Workflow"]:
                self.assertIn(section, body, key)
        for key in epics:
            self.assertIn("## Children", self.github.issue(key)["body"], key)
        self.assertIn("## Epics", self.github.issue(program)["body"])
        self.assertEqual(expected, set(json.loads(self.state.read_text(encoding="utf-8"))["issues"]))

    def test_create_interruption_and_lost_acknowledgement_resume_without_duplicates(self):
        for committed in [False, True]:
            with self.subTest(create_committed_before_failure=committed):
                self.github = FakeGitHub()
                self.github.fail_create = "H-01"
                self.github.commit_before_failure = committed
                with self.assertRaisesRegex(RuntimeError, "injected"):
                    self.publish()
                original = self.github.issue("C-01")
                self.assertNotIn("## Acceptance", original["body"])
                # Exact skeleton recognition permits only newline/outer-whitespace changes.
                original["body"] = " \r\n" + original["body"].replace("\n", "\r\n") + "\r\n "
                self.publish()
                self.assert_fully_expanded()
                completed = copy.deepcopy(self.github.issues)
                edits = list(self.github.edits)
                self.publish()
                self.assertEqual(completed, self.github.issues)
                self.assertEqual(edits, self.github.edits)

    def test_existing_human_edited_and_closed_skeletons_are_preserved(self):
        self.github.fail_edit = "C-01"
        with self.assertRaisesRegex(RuntimeError, "expansion failure"):
            self.publish()
        human = self.github.issue("C-01")
        human["body"] += "\nClaimed by a human; preserve current acceptance evidence.\n"
        closed = self.github.issue("C-02")
        closed["state"] = "CLOSED"
        protected = {key: copy.deepcopy(self.github.issue(key)) for key in ["C-01", "C-02"]}
        self.publish()
        for key, expected in protected.items():
            self.assertEqual(expected, self.github.issue(key))
            self.assertNotIn(key, self.github.edits)
        self.assertIn("## Acceptance", self.github.issue("H-01")["body"])
        self.assertEqual(len(self.github.issues), len(self.github.initial_bodies))

    def test_edit_or_closure_between_tracking_and_expansion_vetoes_overwrite(self):
        protected = {}

        def changed_after_tracking(item, key):
            if key == "C-01":
                item["body"] += "\nNew human claim after initial discovery.\n"
            elif key == "C-02":
                item["state"] = "CLOSED"
            else:
                return
            protected[key] = copy.deepcopy(item)

        self.github.on_view = changed_after_tracking
        self.publish()
        self.assertEqual({"C-01", "C-02"}, set(protected))
        for key, expected in protected.items():
            self.assertEqual(expected, self.github.issue(key))
            self.assertNotIn(key, self.github.edits)
        self.assertIn("## Acceptance", self.github.issue("H-01")["body"])


if __name__ == "__main__":
    unittest.main()
