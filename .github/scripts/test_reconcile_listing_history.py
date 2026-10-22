import copy
import importlib.util
from pathlib import Path
import unittest


spec = importlib.util.spec_from_file_location("reconcile_listing", Path(__file__).with_name("reconcile-listing-history.py"))
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class HistoryTests(unittest.TestCase):
    def setUp(self):
        self.source = {"id": "com.ajisaiflow.vpm", "name": "AjisaiFlow", "url": "https://lighfu.github.io/vpm/index.json", "githubRepos": ["lighfu/vpm"], "packages": []}
        self.live = self.listing("1.0.0")
        self.generated = self.listing("2.0.0-beta.1")

    def listing(self, version):
        return {key: value for key, value in self.source.items() if key != "githubRepos" and key != "packages"} | {
            "packages": {"example.package": {"versions": {version: {
                "name": "example.package", "version": version, "description": "Published metadata",
                "url": f"https://github.com/lighfu/vpm/releases/download/v{version}/package.zip", "zipSHA256": "a" * 64}}}}}

    def test_retains_every_published_value_and_adds_expected_release(self):
        original = copy.deepcopy(self.live)
        merged, counts = module.reconcile(self.source, self.generated, self.live, "example.package", "2.0.0-beta.1", "a" * 64)
        self.assertEqual(counts, {"generated": 1, "live": 1, "retained": 1, "total": 2})
        self.assertEqual(merged["packages"]["example.package"]["versions"]["1.0.0"], self.live["packages"]["example.package"]["versions"]["1.0.0"])
        self.assertEqual(self.live, original)

    def test_published_nonidentity_values_remain_verbatim(self):
        changed = self.listing("1.0.0")
        changed["packages"]["example.package"]["versions"]["1.0.0"]["description"] = "Regenerated description"
        merged, _ = module.reconcile(self.source, changed, self.live)
        self.assertEqual(merged, self.live)

    def test_refuses_different_listing_identity(self):
        for key in ("id", "name", "url"):
            with self.subTest(key=key):
                other = copy.deepcopy(self.live)
                other[key] = "different"
                with self.assertRaises(ValueError):
                    module.reconcile(self.source, self.generated, other)

    def test_refuses_published_url_or_hash_conflicts(self):
        for key, value in (("url", "https://github.com/lighfu/vpm/releases/download/replaced/package.zip"), ("zipSHA256", "b" * 64)):
            with self.subTest(key=key):
                changed = self.listing("1.0.0")
                changed["packages"]["example.package"]["versions"]["1.0.0"][key] = value
                with self.assertRaises(ValueError):
                    module.reconcile(self.source, changed, self.live)

    def test_refuses_expected_release_missing_wrong_hash_or_partial_inputs(self):
        for version, digest in (("missing", "a" * 64), ("2.0.0-beta.1", "b" * 64), ("", "a" * 64)):
            with self.subTest(version=version, digest=digest):
                with self.assertRaises(ValueError):
                    module.reconcile(self.source, self.generated, self.live, "example.package", version, digest)

    def test_refuses_foreign_assets_malformed_manifests_and_empty_generation(self):
        for key, value in (("url", "https://github.com/other/repo/releases/download/v1/package.zip"), ("name", "other.package"), ("version", "other-version"), ("zipSHA256", "bad")):
            with self.subTest(key=key):
                changed = copy.deepcopy(self.generated)
                changed["packages"]["example.package"]["versions"]["2.0.0-beta.1"][key] = value
                with self.assertRaises(ValueError):
                    module.reconcile(self.source, changed, self.live)
        empty = copy.deepcopy(self.generated)
        empty["packages"] = {}
        with self.assertRaises(ValueError):
            module.reconcile(self.source, empty, self.live)


if __name__ == "__main__":
    unittest.main()
