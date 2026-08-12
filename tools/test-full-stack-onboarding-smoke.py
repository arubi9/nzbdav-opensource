#!/usr/bin/env python3
"""Focused contract tests for the browser-only onboarding smoke harness."""
from __future__ import annotations

import copy
import importlib.util
import json
import subprocess
import unittest
from pathlib import Path
from unittest.mock import Mock, patch

SCRIPT = Path(__file__).with_name("full-stack-onboarding-smoke.py")
spec = importlib.util.spec_from_file_location("onboarding_smoke", SCRIPT)
assert spec and spec.loader
smoke = importlib.util.module_from_spec(spec)
spec.loader.exec_module(smoke)
ROOT = SCRIPT.parent.parent


class _Response:
    headers = {}

    def __init__(self, body: str = "", status: int = 200) -> None:
        self.body = body.encode()
        self.status = status

    def read(self, *_: object) -> bytes:
        return self.body

    def __enter__(self) -> "_Response": return self
    def __exit__(self, *_: object) -> None: pass


class OnboardingHarnessTests(unittest.TestCase):
    def test_redactor_removes_registered_and_named_secrets(self) -> None:
        redactor = smoke.Redactor(["user-secret"])
        redactor.add("response-grant")
        value = redactor.redact({"password": "user-secret", "detail": "response-grant", "nested": ["user-secret"]})
        self.assertEqual(value["password"], "[REDACTED]")
        self.assertEqual(value["detail"], "[REDACTED]")
        self.assertEqual(value["nested"], ["[REDACTED]"])

    def test_run_decodes_non_utf8_diagnostics_as_utf8_and_retains_only_sanitized_evidence(self) -> None:
        harness = smoke.Harness(False)
        secret = "diagnostic-secret"
        harness.redactor.add(secret)
        try:
            result = harness.run(
                [smoke.sys.executable, "-c", f"import sys; sys.stdout.buffer.write(bytes([98, 97, 100, 255, 32]) + b'{secret}')"],
                check=False)
            self.assertEqual(result.stdout, f"bad� {secret}")
            harness.record("non-utf8-diagnostic", output=result.stdout)
            evidence = (harness.artifacts / "evidence.json").read_text(encoding="utf-8")
            self.assertEqual(json.loads(evidence)["phases"][0]["output"], "bad� [REDACTED]")
            self.assertNotIn(secret, evidence)
        finally:
            import shutil
            shutil.rmtree(harness.artifacts, ignore_errors=True)

    def test_fake_newznab_contract_has_public_caps_and_authenticated_search(self) -> None:
        self.assertIn('fields[2] == USER', smoke.FAKE_PROGRAM)
        self.assertIn('fields[2] == PASSWORD', smoke.FAKE_PROGRAM)
        self.assertIn('q.get("apikey", [None])[0] == APIKEY', smoke.FAKE_PROGRAM)
        self.assertIn('op == "caps"', smoke.FAKE_PROGRAM)
        self.assertIn('newznab-caps-unauthenticated', smoke.FAKE_PROGRAM)
        self.assertIn('if not ok: self.send_error(403); return', smoke.FAKE_PROGRAM)
        self.assertIn('parsed.path == "/audit"', smoke.FAKE_PROGRAM)
        self.assertNotIn('"value"', smoke.FAKE_PROGRAM)

    def test_fake_contract_matches_newznab_capability_client_uri_semantics(self) -> None:
        source = (ROOT / "backend/Clients/Newznab/NewznabCapabilityClient.cs").read_text(encoding="utf-8")
        # The production capability probe appends exactly t=caps and no key.
        self.assertIn('builder.Query = string.IsNullOrEmpty(existingQuery) ? "t=caps"', source)
        self.assertIn('BuildCapabilityUri(credential.BaseUrl)', source)
        self.assertIn('NewznabCapabilityStatus.InvalidCredentials', source)

    def test_browser_flow_contains_no_private_setup_api_auth(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("host.docker.internal", source)
        self.assertNotIn("bootstrap-secrets", source)
        self.assertIn('"action": "handoff"', source)
        self.assertIn('"action": "repair-handoff"', source)
        self.assertIn('"action": "repair"', source)

    def test_jellyfin_startup_user_readiness_gets_before_credential_post(self) -> None:
        harness = smoke.Harness(False)
        calls: list[tuple[str, str, bytes | None]] = []

        def urlopen(request: object, **_: object) -> _Response:
            if isinstance(request, str):
                calls.append(("GET", request, None))
            else:
                calls.append((request.get_method(), request.full_url, request.data))
            url = calls[-1][1]
            if url.endswith("/Users/AuthenticateByName"):
                return _Response(json.dumps({"AccessToken": "test-token"}))
            if url.endswith("/health") or (url.endswith("/Startup/User") and calls[-1][0] == "GET"):
                return _Response()
            return _Response(status=204)

        with patch.object(smoke.urllib.request, "urlopen", side_effect=urlopen):
            harness.bootstrap_jellyfin()
        startup_user = [call for call in calls if call[1].endswith("/Startup/User")]
        self.assertEqual([call[0] for call in startup_user], ["GET", "POST"])
        self.assertEqual(json.loads(startup_user[1][2].decode()), {"Name": smoke.ADMIN_USER, "Password": harness.admin_password})

    def test_fake_audit_command_never_runs_python_in_nzbdav_image(self) -> None:
        harness = smoke.Harness(False)
        command = harness.fake_audit_command()
        self.assertEqual(command[:3], ["docker", "exec", harness.fake_name])
        self.assertIn("wget", command)
        self.assertNotIn("nzbdav", command)
        self.assertNotIn("python", command)

    def test_ephemeral_jellyfin_host_binding_keeps_a_valid_plugin_public_port(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn('"JELLYFIN_PUBLIC_PORT": "8096"', source)

    def test_private_newznab_fake_uses_a_private_ipv4_literal_for_http(self) -> None:
        harness = smoke.Harness(False)
        harness.fake_stack_ip = "172.23.0.9"

        self.assertEqual(harness.fake_indexer_url(), "http://172.23.0.9:8080")

    def test_compose_start_uses_explicit_dependency_tier_health_checks(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("self.start_stack(env)", source)

    def test_execute_verifies_completed_services_before_first_ready_assertion(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        configured = source.index('self.record("configured-through-browser"')
        verify = source.index('self.action({"action": "verify-services"}, timeout=240)', configured)
        first_ready = source.index('self.assert_ready()', configured)
        self.assertLess(verify, first_ready)

    def test_starts_dependency_tiers_without_compose_health_race(self) -> None:
        harness = smoke.Harness(False)
        harness.run = Mock(return_value=subprocess.CompletedProcess([], 0, ""))
        harness.wait_services = Mock()

        harness.start_stack({"BIND_ADDRESS": "127.0.0.1"})

        self.assertEqual(
            [call.args[0] for call in harness.run.call_args_list],
            [
                harness.compose + ["up", "--build", "--detach", "sonarr", "radarr", "prowlarr"],
                harness.compose + ["up", "--build", "--detach", "--no-deps", "nzbdav"],
                harness.compose + ["up", "--build", "--detach", "--no-deps", "jellyfin"],
            ],
        )
        self.assertEqual(
            [call.args[0] for call in harness.wait_services.call_args_list],
            [("sonarr", "radarr", "prowlarr"), ("nzbdav",), ("jellyfin",)],
        )

    def test_discovers_docker_assigned_loopback_ports_after_compose_up(self) -> None:
        harness = smoke.Harness(False)
        expected = dict(zip(smoke.SERVICES, [31001, 31002, 31003, 31004, 31005], strict=True))
        harness.compose_run = Mock(side_effect=[
            subprocess.CompletedProcess([], 0, f"127.0.0.1:{expected[service]}\n")
            for service in smoke.SERVICES
        ])

        harness.discover_published_ports()

        self.assertEqual(harness.ports, expected)
        self.assertEqual(
            [call.args[0] for call in harness.compose_run.call_args_list],
            [["port", service, str(port)] for service, port in zip(smoke.SERVICES, [3000, 8096, 8989, 7878, 9696], strict=True)],
        )

    def test_restart_reresolves_ports_and_waits_for_public_frontend_before_final_status(self) -> None:
        harness = smoke.Harness(False)
        harness.compose_run = Mock(return_value=subprocess.CompletedProcess([], 0, ""))
        harness.discover_published_ports = Mock()
        harness.wait_healthy = Mock()
        harness.wait_for_public_frontend = Mock()

        harness.restart_and_wait_for_public_frontend()

        harness.compose_run.assert_called_once_with(["restart"], timeout=180)
        harness.discover_published_ports.assert_called_once_with()
        harness.wait_healthy.assert_called_once_with(300)
        harness.wait_for_public_frontend.assert_called_once_with()

    def test_public_frontend_readiness_retries_only_connection_refused(self) -> None:
        harness = smoke.Harness(False)
        harness.browser_json = Mock(side_effect=[
            smoke.urllib.error.URLError(ConnectionRefusedError(111, "connection refused")),
            {"completed": True},
        ])

        with patch.object(smoke.time, "sleep"):
            harness.wait_for_public_frontend(timeout=5)

        self.assertEqual(harness.browser_json.call_count, 2)

    def test_public_frontend_readiness_does_not_retry_http_contract_failure(self) -> None:
        harness = smoke.Harness(False)
        harness.browser_json = Mock(side_effect=smoke.Failure("browser request /api/setup/status expected 200, got 401"))

        with self.assertRaisesRegex(smoke.Failure, "expected 200, got 401"):
            harness.wait_for_public_frontend(timeout=5)

        harness.browser_json.assert_called_once()

    def test_project_label_cleanup_lists_only_its_project_resources(self) -> None:
        harness = smoke.Harness(False)
        harness.run = Mock(return_value=subprocess.CompletedProcess([], 0, "one\ntwo\n"))
        self.assertEqual(harness.labeled_ids("volume"), ["one", "two"])
        command = harness.run.call_args.args[0]
        self.assertEqual(command[:4], ["docker", "volume", "ls", "-q"])
        self.assertIn(f"label=com.docker.compose.project={harness.project}", command)

    def test_cleanup_continues_after_failure_and_reports_leak(self) -> None:
        harness = smoke.Harness(False)
        commands: list[str] = []
        harness.cleanup_command = lambda description, *_: commands.append(description)  # type: ignore[method-assign]
        # First call per kind gathers owned resources; second verifies leak.
        harness.labeled_ids = Mock(side_effect=[[], ["leaked-container"], [], [], [], []])
        harness.run = Mock(return_value=subprocess.CompletedProcess([], 1, ""))
        harness.cleanup()
        self.assertEqual(commands[:4], ["compose down", "stop fake", "remove fake", "remove fake network"])
        self.assertIn("leaked owned containers: ['leaked-container']", harness.cleanup_errors)

    def test_cleanup_command_records_nonzero_return(self) -> None:
        harness = smoke.Harness(False)
        harness.run = Mock(return_value=subprocess.CompletedProcess([], 7, "failed"))
        harness.cleanup_command("remove fake", ["docker", "rm", "fake"], 1)
        self.assertIn("remove fake returned 7", harness.cleanup_errors)

    def test_library_and_arr_snapshots_reject_duplicate_managed_resources(self) -> None:
        duplicate_library = {"libraries": [{"name": "NZBDAV TV", "id": "one"}, {"name": "NZBDAV TV", "id": "two"}]}
        with self.assertRaises(smoke.Failure): smoke.Harness.library_ids(duplicate_library)
        duplicate_arr = {"sonarr_download_clients": [{"id": 1}, {"id": 1}], "sonarr_root_folders": [{"id": 10}], "sonarr_download_client_testall": [], "radarr_download_clients": [{"id": 2}], "radarr_root_folders": [{"id": 20}], "radarr_download_client_testall": [], "prowlarr_indexers": [{"id": 3}], "prowlarr_applications": [{"id": 4}, {"id": 5}], "prowlarr_indexer_testall": [], "prowlarr_applications_testall": []}
        with self.assertRaises(smoke.Failure): smoke.Harness.assert_arr_counts(duplicate_arr)

    def test_service_local_snapshot_rejects_malicious_or_accidental_secret_output(self) -> None:
        safe = {"download_clients": [], "root_folders": [], "download_client_testall": []}
        self.assertEqual(smoke.Harness._parse_local_snapshot("sonarr", json.dumps(safe)), safe)
        for poisoned in (
            {**safe, "apiKey": "copied-product-secret"},
            {"download_clients": [{"id": 1, "name": "NZBDAV Sonarr", "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "enable": True, "password": "copied-product-secret"}], "root_folders": [], "download_client_testall": []},
            {"download_clients": "", "root_folders": "", "download_client_testall": ""},
            {"download_clients": [{"id": True, "name": "NZBDAV Sonarr", "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "enable": True}], "root_folders": [], "download_client_testall": []},
            {"download_clients": [{"id": 1, "name": "0123456789abcdef0123456789abcdef", "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "enable": True}], "root_folders": [], "download_client_testall": []},
            {"download_clients": [{"id": 1, "name": "Bearer eyJhbGciOiJIUzI1NiJ9", "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "enable": True}], "root_folders": [], "download_client_testall": []},
            {"download_clients": [{"id": 1, "name": "abc\u0000def", "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "enable": True}], "root_folders": [], "download_client_testall": []},
        ):
            with self.assertRaisesRegex(smoke.Failure, "unsafe|secret|invalid"):
                smoke.Harness._parse_local_snapshot("sonarr", json.dumps(poisoned))

    def test_jellyfin_library_contract_requires_type_and_location(self) -> None:
        valid = {"libraries": [
            {"id": "movies", "name": "NZBDAV Movies", "collectionType": "movies", "locations": ["/media/nzbdav/movies"]},
            {"id": "tv", "name": "NZBDAV TV", "collectionType": "tvshows", "locations": ["/media/nzbdav/tv"]},
        ]}
        self.assertEqual(smoke.Harness.library_ids(valid), {"NZBDAV Movies": "movies", "NZBDAV TV": "tv"})
        for invalid in (
            {"libraries": [valid["libraries"][0], {**valid["libraries"][1], "collectionType": "movies"}]},
            {"libraries": [valid["libraries"][0], {**valid["libraries"][1], "locations": []}]},
        ):
            with self.assertRaises(smoke.Failure): smoke.Harness.library_ids(invalid)

    def test_arr_contract_requires_pinned_prowlarr_pairings(self) -> None:
        snapshot = {"sonarr_download_clients": [{"id": 1, "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "enable": True}], "sonarr_root_folders": [{"id": 2, "path": "/data/completed-downloads/tv"}], "sonarr_download_client_testall": [{"id": 1, "isValid": True}], "radarr_download_clients": [{"id": 3, "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "enable": True}], "radarr_root_folders": [{"id": 4, "path": "/data/completed-downloads/movies"}], "radarr_download_client_testall": [{"id": 3, "isValid": True}], "prowlarr_indexers": [{"id": 5, "name": "NZBDAV E2E Newznab", "implementation": "Newznab", "configContract": "NewznabSettings", "enable": True}], "prowlarr_applications": [{"id": 6, "name": "NZBDAV Sonarr", "implementation": "Sonarr", "configContract": "SonarrSettings", "enable": True, "syncLevel": "fullSync"}, {"id": 7, "name": "NZBDAV Radarr", "implementation": "Radarr", "configContract": "RadarrSettings", "enable": True, "syncLevel": "fullSync"}], "prowlarr_indexer_testall": [{"id": 5, "isValid": True}], "prowlarr_applications_testall": [{"id": 6, "isValid": True}, {"id": 7, "isValid": True}]}
        smoke.Harness.assert_arr_counts(snapshot)
        snapshot["prowlarr_indexers"][0]["configContract"] = "SonarrSettings"
        with self.assertRaises(smoke.Failure): smoke.Harness.assert_arr_counts(snapshot)

    def test_arr_testall_requires_exact_complete_valid_managed_identity_set(self) -> None:
        valid = {"sonarr_download_clients": [{"id": 1, "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "enable": True}], "sonarr_root_folders": [{"id": 2, "path": "/data/completed-downloads/tv"}], "sonarr_download_client_testall": [{"id": 1, "isValid": True}], "radarr_download_clients": [{"id": 3, "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "enable": True}], "radarr_root_folders": [{"id": 4, "path": "/data/completed-downloads/movies"}], "radarr_download_client_testall": [{"id": 3, "isValid": True}], "prowlarr_indexers": [{"id": 5, "name": "NZBDAV E2E Newznab", "implementation": "Newznab", "configContract": "NewznabSettings", "enable": True}], "prowlarr_applications": [{"id": 6, "name": "NZBDAV Sonarr", "implementation": "Sonarr", "configContract": "SonarrSettings", "enable": True, "syncLevel": "fullSync"}, {"id": 7, "name": "NZBDAV Radarr", "implementation": "Radarr", "configContract": "RadarrSettings", "enable": True, "syncLevel": "fullSync"}], "prowlarr_indexer_testall": [{"id": 5, "isValid": True}], "prowlarr_applications_testall": [{"id": 6, "isValid": True}, {"id": 7, "isValid": True}]}
        smoke.Harness.assert_arr_counts(valid)
        cases = (
            # Unknown rows must not be accepted beside an otherwise valid ID.
            ("sonarr_download_client_testall", lambda rows: rows.append({"id": 99, "isValid": True})),
            # Managed resources need a corresponding result.
            ("prowlarr_applications_testall", lambda rows: rows.pop()),
            # Repeated successful IDs do not prove the full set.
            ("radarr_download_client_testall", lambda rows: rows.append({"id": 3, "isValid": True})),
            # Conflicting results for an ID must be rejected, not masked.
            ("prowlarr_indexer_testall", lambda rows: rows.append({"id": 5, "isValid": False})),
            # The sole row must be valid.
            ("sonarr_download_client_testall", lambda rows: rows.__setitem__(0, {"id": 1, "isValid": False})),
        )
        for field, mutate in cases:
            with self.subTest(field=field, mutate=mutate):
                invalid = copy.deepcopy(valid)
                mutate(invalid[field])
                with self.assertRaisesRegex(smoke.Failure, "exactly one valid result"):
                    smoke.Harness.assert_arr_counts(invalid)

    def test_complete_managed_identity_map_rejects_library_and_arr_id_churn(self) -> None:
        snapshot = {"libraries": [
            {"id": "movies", "name": "NZBDAV Movies", "collectionType": "movies", "locations": ["/media/nzbdav/movies"]},
            {"id": "tv", "name": "NZBDAV TV", "collectionType": "tvshows", "locations": ["/media/nzbdav/tv"]},
        ], "sonarr_download_clients": [{"id": 1, "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "enable": True}], "sonarr_root_folders": [{"id": 2, "path": "/data/completed-downloads/tv"}], "sonarr_download_client_testall": [{"id": 1, "isValid": True}], "radarr_download_clients": [{"id": 3, "implementation": "Sabnzbd", "configContract": "SabnzbdSettings", "enable": True}], "radarr_root_folders": [{"id": 4, "path": "/data/completed-downloads/movies"}], "radarr_download_client_testall": [{"id": 3, "isValid": True}], "prowlarr_indexers": [{"id": 5, "name": "NZBDAV E2E Newznab", "implementation": "Newznab", "configContract": "NewznabSettings", "enable": True}], "prowlarr_applications": [{"id": 6, "name": "NZBDAV Sonarr", "implementation": "Sonarr", "configContract": "SonarrSettings", "enable": True, "syncLevel": "fullSync"}, {"id": 7, "name": "NZBDAV Radarr", "implementation": "Radarr", "configContract": "RadarrSettings", "enable": True, "syncLevel": "fullSync"}], "prowlarr_indexer_testall": [{"id": 5, "isValid": True}], "prowlarr_applications_testall": [{"id": 6, "isValid": True}, {"id": 7, "isValid": True}]}
        canonical = smoke.Harness.managed_identity_map(snapshot)
        self.assertEqual(canonical["jellyfin_libraries"], {"NZBDAV Movies": "movies", "NZBDAV TV": "tv"})
        for path, value in ((["libraries", 1, "id"], "new-tv"), (["sonarr_download_clients", 0, "id"], 11), (["radarr_root_folders", 0, "id"], 14), (["prowlarr_indexers", 0, "id"], 15), (["prowlarr_applications", 1, "id"], 17)):
            changed = copy.deepcopy(snapshot)
            target: object = changed
            for key in path[:-1]:
                target = target[key]  # type: ignore[index]
            target[path[-1]] = value  # type: ignore[index]
            # Keep each changed resource's testall row valid so this proves
            # identity equality, rather than merely testall consistency.
            if path[0] == "sonarr_download_clients": changed["sonarr_download_client_testall"][0]["id"] = value
            if path[0] == "prowlarr_indexers": changed["prowlarr_indexer_testall"][0]["id"] = value
            if path[0] == "prowlarr_applications": changed["prowlarr_applications_testall"][1]["id"] = value
            actual = smoke.Harness.managed_identity_map(changed)
            with self.subTest(path=path):
                with self.assertRaisesRegex(smoke.Failure, "changed complete managed identity"):
                    smoke.Harness.assert_managed_identity_equal(canonical, actual, "repair")

    def test_prowlarr_application_projection_requires_full_sync(self) -> None:
        safe = {"indexers": [], "applications": [
            {"id": 1, "name": "NZBDAV Sonarr", "implementation": "Sonarr", "configContract": "SonarrSettings", "enable": True, "syncLevel": "fullSync"},
            {"id": 2, "name": "NZBDAV Radarr", "implementation": "Radarr", "configContract": "RadarrSettings", "enable": True, "syncLevel": "fullSync"},
        ], "indexer_testall": [], "applications_testall": []}
        self.assertEqual(smoke.Harness._parse_local_snapshot("prowlarr", json.dumps(safe)), safe)
        for sync_level in ("addOnly", "unknown", True):
            invalid = json.loads(json.dumps(safe))
            invalid["applications"][0]["syncLevel"] = sync_level
            with self.assertRaisesRegex(smoke.Failure, "invalid sync level"):
                smoke.Harness._parse_local_snapshot("prowlarr", json.dumps(invalid))
        missing = json.loads(json.dumps(safe))
        del missing["applications"][0]["syncLevel"]
        with self.assertRaisesRegex(smoke.Failure, "unsafe resource"):
            smoke.Harness._parse_local_snapshot("prowlarr", json.dumps(missing))

    def test_normalized_compose_topology_requires_exact_volumes_and_mounts(self) -> None:
        services = {name: {"volumes": []} for name in smoke.SERVICES}
        for service, source, target, read_only in smoke.REQUIRED_VOLUME_MOUNTS:
            services[service]["volumes"].append({"type": "volume", "source": source, "target": target, "read_only": read_only})
        config = {"services": services, "volumes": {name: {} for name in smoke.CANONICAL_VOLUMES}}
        proof = smoke.assert_normalized_compose_topology(config)
        self.assertEqual(proof["named_volume_count"], 8)
        del config["volumes"]["jellyfin_cache"]
        with self.assertRaises(smoke.Failure): smoke.assert_normalized_compose_topology(config)

    def test_servarr_credentials_never_cross_the_harness_exec_boundary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("service_api_key", source)
        self.assertNotIn("service_json", source)
        # The only service verification command is Compose exec. Parent-side
        # urllib must not contact a Servarr host port or construct its header.
        self.assertNotRegex(source, r"urllib\.request\.Request\(f?[^\n]*ports\[service\]")
        self.assertIn('compose_run(["exec", "-T", service, "sh", "-ec", script]', source)
        self.assertIn('exec 2>/dev/null', source)
        self.assertIn('jq -cn', source)


if __name__ == "__main__":
    unittest.main()
