"""Offline deployment contract checks: Python 3 + PyYAML, kubectl on PATH."""
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

import yaml

ROOT = Path(__file__).resolve().parents[1]


def validate(directory, expected_host):
    rendered = subprocess.check_output(
        ["kubectl", "kustomize", str(directory)], text=True, encoding="utf-8"
    )
    resources = list(yaml.safe_load_all(rendered))
    by_kind = {item["kind"]: item for item in resources if item["kind"] != "ConfigMap"}
    deployment = by_kind["Deployment"]
    container = deployment["spec"]["template"]["spec"]["containers"][0]
    # The HPA owns the replica count; a fixed value would be re-applied on every deploy.
    assert "replicas" not in deployment["spec"]
    env = {item["name"]: item.get("value") for item in container["env"]}
    ports = {item["name"]: item["containerPort"] for item in container["ports"]}
    assert ports == {"http": 8080, "management": 8081}
    assert env["ASPNETCORE_HTTP_PORTS"] == "8080;8081" and env["HealthChecks__ManagementPort"] == "8081"
    for probe in ("startupProbe", "livenessProbe", "readinessProbe"):
        headers = {item["name"]: item["value"] for item in container[probe]["httpGet"]["httpHeaders"]}
        assert headers["Host"] == expected_host, probe
        assert container[probe]["httpGet"]["port"] == "management", probe
    # Health stays private: only the traffic port is published, and the Ingress forwards everything to it.
    assert [item["targetPort"] for item in by_kind["Service"]["spec"]["ports"]] == ["http"]
    assert [item["port"] for item in by_kind["NetworkPolicy"]["spec"]["ingress"][0]["ports"]] == [8080]
    ingress = by_kind["Ingress"]["spec"]
    assert ingress["rules"][0]["host"] == expected_host
    assert ingress["tls"][0]["hosts"] == [expected_host]
    assert [item["path"] for item in ingress["rules"][0]["http"]["paths"]] == ["/"]
    hpa = by_kind["HorizontalPodAutoscaler"]["spec"]
    assert hpa["minReplicas"] >= by_kind["PodDisruptionBudget"]["spec"]["minAvailable"]
    assert hpa["maxReplicas"] >= hpa["minReplicas"]
    assert container["securityContext"]["readOnlyRootFilesystem"] is True
    assert deployment["spec"]["template"]["spec"]["automountServiceAccountToken"] is False
    assert {"secretRef": {"name": "core-gateway-jwt-providers", "optional": True}} in container["envFrom"]
    assert {"secretRef": {"name": "core-gateway-api-keys", "optional": True}} in container["envFrom"]
    for setting in ("Authentication__Jwt__Authority", "Authentication__Jwt__Audience"):
        auth = next(item for item in container["env"] if item["name"] == setting)
        assert auth["valueFrom"]["secretKeyRef"]["optional"] is True
    assert by_kind["NetworkPolicy"]["spec"]["podSelector"] == deployment["spec"]["selector"]
    config_maps = [item for item in resources if item["kind"] == "ConfigMap"]
    assert all(item["immutable"] is True for item in config_maps)
    settings = next(item["data"] for item in config_maps if "RateLimiting__Local__Enabled" in item["data"])
    assert settings["RateLimiting__Local__Enabled"] == "true"
    assert int(settings["RateLimiting__Local__Instance__PermitLimit"]) > 0
    assert int(settings["RateLimiting__Local__Instance__WindowSeconds"]) > 0
    # Per-user quotas must be shared, or each new pod multiplies them.
    assert settings["RateLimiting__Distributed__Enabled"] == "true"
    assert int(settings["RateLimiting__Distributed__Authenticated__PermitLimit"]) > 0
    assert int(settings["RateLimiting__Distributed__Anonymous__PermitLimit"]) > 0
    assert settings["Resilience__RequestTimeoutSeconds"] == "30"
    assert settings["Resilience__ConnectTimeoutSeconds"] == "5"
    assert int(settings["Resilience__ActivityTimeoutSeconds"]) > 0
    assert int(settings["Resilience__PooledConnectionLifetimeSeconds"]) > 0
    assert int(settings["RateLimiting__Local__ServiceConcurrencyLimit"]) <= int(settings["RateLimiting__Local__InstanceConcurrencyLimit"])
    assert settings["Resilience__BackendHealthDetectionWindowSeconds"] == "10"
    assert settings["Resilience__BackendHealthReactivationSeconds"] == "11"
    assert settings["Resilience__FailWhenAllDestinationsUnhealthy"] == "true"
    redis = next(item for item in container["env"] if item["name"] == "Redis__ConnectionString")
    assert redis["valueFrom"]["secretKeyRef"] == {"name": "core-gateway-redis", "key": "connection-string", "optional": True}
    routes = json.loads(next(item["data"]["reverseproxy.json"] for item in config_maps if "reverseproxy.json" in item["data"]))
    return routes["ReverseProxy"]


def validate_inflight_component(base):
    # Kustomize only accepts relative references, so render an overlay next to a copy of the base.
    with tempfile.TemporaryDirectory(prefix="gateway-component-test-") as temporary:
        shutil.copytree(base, Path(temporary) / "base")
        overlay = Path(temporary) / "overlay"
        overlay.mkdir()
        (overlay / "kustomization.yaml").write_text(yaml.safe_dump({
            "apiVersion": "kustomize.config.k8s.io/v1beta1",
            "kind": "Kustomization",
            "resources": ["../base"],
            "components": ["../base/components/inflight-autoscaling"],
        }), encoding="utf-8")
        rendered = subprocess.check_output(["kubectl", "kustomize", str(overlay)], text=True, encoding="utf-8")
    hpa = next(item for item in yaml.safe_load_all(rendered) if item["kind"] == "HorizontalPodAutoscaler")
    names = [metric.get("resource", metric.get("pods", {}).get("metric", {})).get("name") for metric in hpa["spec"]["metrics"]]
    assert names == ["cpu", "gateway_inflight_requests"], names


def validate_routes(proxy):
    for name, path in (("user", "users"), ("order", "orders"), ("product", "products")):
        canonical = proxy["Routes"][f"{name}-route"]
        legacy = proxy["Routes"][f"{name}-legacy-route"]
        assert canonical["Match"]["Path"] == f"/api/{path}/{{**catch-all}}"
        assert legacy["Match"]["Path"] == f"/{path}/{{**catch-all}}"
        assert canonical["ClusterId"] == legacy["ClusterId"] == f"{name}-cluster"
        assert canonical["TimeoutPolicy"] == legacy["TimeoutPolicy"] == "api"
        assert legacy["Transforms"] == [{"PathRemovePrefix": f"/{path}"}, {"PathPrefix": f"/api/{path}"}]
        assert proxy["Clusters"][f"{name}-cluster"]["HttpRequest"]["ActivityTimeout"] == "00:00:20"


if __name__ == "__main__":
    base = ROOT / "deploy" / "kubernetes"
    deployed = validate(base, "gateway.example.com")
    validate_routes(deployed)
    validate_inflight_component(base)
    for filename in ("reverseproxy.json", "reverseproxy.Development.json"):
        validate_routes(json.loads((ROOT / "src" / "Core.Gateway.Api" / "Configuration" / filename).read_text(encoding="utf-8"))["ReverseProxy"])
    # Kustomize cannot load files outside its directory, so the routes are copied; keep both copies identical.
    source = json.loads((ROOT / "src" / "Core.Gateway.Api" / "Configuration" / "reverseproxy.json").read_text(encoding="utf-8"))
    assert deployed == source["ReverseProxy"], "deploy/kubernetes/reverseproxy.json drifted from src/Core.Gateway.Api/Configuration/reverseproxy.json"
    with tempfile.TemporaryDirectory(prefix="gateway-manifest-test-") as temporary:
        target = Path(temporary).resolve()
        assert target.is_relative_to(Path(tempfile.gettempdir()).resolve())
        shutil.copytree(base, target, dirs_exist_ok=True)
        settings = target / "settings.env"
        settings.write_text(settings.read_text(encoding="utf-8").replace("gateway.example.com", "gateway.test"), encoding="utf-8")
        validate_routes(validate(target, "gateway.test"))
    print("Deployment contracts passed: routes, aliases, probes/Ingress host replacement, HPA/PDB, container security.")
