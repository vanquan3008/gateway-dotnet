"""Checks the gateway PrometheusRule with promtool: syntax plus the unit tests in tests/observability.

Runs promtool from the prom/prometheus image, so only Docker, Python 3 and PyYAML are required.
"""
import json
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

import yaml

ROOT = Path(__file__).resolve().parents[1]
PROMTOOL_IMAGE = "prom/prometheus:v3.4.0"


def promtool(workdir, *arguments):
    subprocess.run(
        ["docker", "run", "--rm", "--entrypoint", "promtool", "-v", f"{workdir}:/work", "-w", "/work",
         PROMTOOL_IMAGE, *arguments],
        check=True,
    )


if __name__ == "__main__":
    rule = yaml.safe_load((ROOT / "deploy" / "observability" / "prometheus-rules.yaml").read_text(encoding="utf-8"))
    assert rule["kind"] == "PrometheusRule"
    alerts = [item["alert"] for group in rule["spec"]["groups"] for item in group["rules"]]
    assert len(alerts) == len(set(alerts)), "duplicate alert names"
    for group in rule["spec"]["groups"]:
        for item in group["rules"]:
            assert item["labels"]["severity"] in ("warning", "critical"), item["alert"]
            assert item["annotations"].get("action", "").strip(), f"{item['alert']} needs an action annotation"

    # Every series used by alerts and the dashboard must exist in what the gateway really exports.
    known = {line.strip() for line in (ROOT / "tests" / "observability" / "known-metrics.txt").read_text(encoding="utf-8").splitlines()
             if line.strip() and not line.startswith("#")}
    dashboard_path = ROOT / "deploy" / "observability" / "grafana-dashboard.json"
    dashboard = json.loads(dashboard_path.read_text(encoding="utf-8"))
    expressions = [item["expr"] for group in rule["spec"]["groups"] for item in group["rules"]]
    expressions += [target["expr"] for panel in dashboard["panels"] for target in panel.get("targets", [])]
    functions = {"sum", "rate", "absent", "histogram_quantile", "max", "by", "le"}
    checked = 0
    for expression in expressions:
        for name in re.findall(r"\b([a-z_][a-z0-9_]*)\s*\{", expression):
            if name in functions or name.startswith("kube_"):
                continue
            assert name in known, f"unknown metric {name} in: {expression}"
            checked += 1
    assert checked > 0, "metric name extraction found nothing"
    subprocess.run(["python", str(ROOT / "deploy" / "observability" / "build_dashboard.py")], check=True, capture_output=True)
    assert json.loads(dashboard_path.read_text(encoding="utf-8")) == dashboard, "grafana-dashboard.json is stale; run build_dashboard.py"

    with tempfile.TemporaryDirectory(prefix="gateway-alerts-") as temporary:
        workdir = Path(temporary)
        (workdir / "rules.yaml").write_text(yaml.safe_dump(rule["spec"], sort_keys=False), encoding="utf-8")
        shutil.copy(ROOT / "tests" / "observability" / "alerts.test.yaml", workdir / "alerts.test.yaml")
        promtool(workdir, "check", "rules", "rules.yaml")
        promtool(workdir, "test", "rules", "alerts.test.yaml")
    print(f"Observability passed: {len(alerts)} alerts (promtool check + unit tests), {len(expressions)} expressions use known metrics.")
