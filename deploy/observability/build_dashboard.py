"""Generates grafana-dashboard.json for Core.Gateway. Edit this file, not the JSON, then run it."""
import json
from pathlib import Path

JOB = 'job="Core.Gateway.Api"'
APP = f'{JOB}, http_route!~"/health/.*"'
RATE = "$__rate_interval"


def target(expr, legend, ref):
    return {"datasource": {"type": "prometheus", "uid": "${datasource}"}, "expr": expr, "legendFormat": legend, "refId": ref}


def panel(title, description, targets, x, y, w=12, h=8, unit="short", kind="timeseries", thresholds=None, stack=False):
    defaults = {"unit": unit, "color": {"mode": "palette-classic"}}
    if thresholds:
        defaults["thresholds"] = {"mode": "absolute", "steps": thresholds}
        defaults["custom"] = {"thresholdsStyle": {"mode": "line+area"}}
    if stack:
        defaults.setdefault("custom", {})["stacking"] = {"mode": "normal"}
    return {
        "type": kind,
        "title": title,
        "description": description,
        "datasource": {"type": "prometheus", "uid": "${datasource}"},
        "gridPos": {"x": x, "y": y, "w": w, "h": h},
        "fieldConfig": {"defaults": defaults, "overrides": []},
        "options": {"legend": {"displayMode": "table", "placement": "bottom", "calcs": ["mean", "max"]}} if kind == "timeseries" else {},
        "targets": [target(*item, chr(ord("A") + index)) for index, item in enumerate(targets)],
    }


def row(title, y):
    return {"type": "row", "title": title, "collapsed": False, "gridPos": {"x": 0, "y": y, "w": 24, "h": 1}, "panels": []}


def quantile(q, metric, selector, by="le"):
    return f"histogram_quantile({q}, sum by ({by}) (rate({metric}{{{selector}}}[{RATE}])))"


ratio_5xx = (
    f'sum(rate(http_server_request_duration_seconds_count{{{APP}, http_response_status_code=~"5.."}}[{RATE}]))'
    f" / sum(rate(http_server_request_duration_seconds_count{{{APP}}}[{RATE}]))"
)
shed_ratio = (
    f'sum(rate(aspnetcore_rate_limiting_requests_total{{{JOB}, aspnetcore_rate_limiting_result!="acquired"}}[{RATE}]))'
    f" / sum(rate(aspnetcore_rate_limiting_requests_total{{{JOB}}}[{RATE}]))"
)

panels = [
    row("Traffic", 0),
    panel("5xx ratio", "Share of non-health responses that are 5xx. Alerts at 1% (10m) and 5% (5m).",
          [(ratio_5xx, "5xx")], 0, 1, w=6, h=6, unit="percentunit", kind="stat",
          thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 0.01}, {"color": "red", "value": 0.05}]),
    panel("Shed by local breaker", "Share of requests rejected with 429 by the per-pod limits. Alerts above 1%.",
          [(shed_ratio, "429")], 6, 1, w=6, h=6, unit="percentunit", kind="stat",
          thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 0.01}]),
    panel("p99 latency", "Gateway p99 including backend time. Alerts above 1s for 10m.",
          [(quantile(0.99, "http_server_request_duration_seconds_bucket", APP), "p99")], 12, 1, w=6, h=6, unit="s", kind="stat",
          thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 0.5}, {"color": "red", "value": 1}]),
    panel("Ready pods", "Available replicas (kube-state-metrics). PDB keeps at least 2.",
          [('kube_deployment_status_replicas_available{deployment="core-gateway"}', "available")], 18, 1, w=6, h=6, kind="stat",
          thresholds=[{"color": "red", "value": None}, {"color": "green", "value": 2}]),
    panel("Requests per second by status", "All proxied requests, health probes excluded.",
          [(f"sum by (http_response_status_code) (rate(http_server_request_duration_seconds_count{{{APP}}}[{RATE}]))", "{{http_response_status_code}}")],
          0, 7, unit="reqps", stack=True),
    panel("Latency: gateway vs backend", "Gateway server duration against the outgoing backend call. The gap is gateway overhead.",
          [(quantile(0.5, "http_server_request_duration_seconds_bucket", APP), "gateway p50"),
           (quantile(0.99, "http_server_request_duration_seconds_bucket", APP), "gateway p99"),
           (quantile(0.99, "http_client_request_duration_seconds_bucket", JOB), "backend p99")],
          12, 7, unit="s"),
    row("Backends", 15),
    panel("Forwarding errors by cluster", "YARP failures: RequestTimedOut = slow backend, Request/Connect = unreachable.",
          [(f"sum by (cluster, error) (rate(gateway_proxy_errors_total{{{JOB}}}[{RATE}]))", "{{cluster}} {{error}}")],
          0, 16, unit="reqps"),
    panel("Backend p99 by service", "Outgoing call duration per backend host.",
          [(quantile(0.99, "http_client_request_duration_seconds_bucket", JOB, by="le, server_address"), "{{server_address}}")],
          12, 16, unit="s"),
    panel("In-flight requests by cluster", "Concurrency bulkhead usage; the limit is 256 per cluster per pod.",
          [(f"sum by (cluster) (gateway_concurrency_active{{{JOB}}})", "{{cluster}}")], 0, 24, unit="short",
          thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 256}]),
    panel("Rejections: 429 sources", "Local rate breaker (by limiter) and concurrency bulkhead (by cluster).",
          [(f'sum by (aspnetcore_rate_limiting_result) (rate(aspnetcore_rate_limiting_requests_total{{{JOB}, aspnetcore_rate_limiting_result!="acquired"}}[{RATE}]))', "rate {{aspnetcore_rate_limiting_result}}"),
           (f"sum by (cluster) (rate(gateway_concurrency_rejected_total{{{JOB}}}[{RATE}]))", "concurrency {{cluster}}")],
          12, 24, unit="reqps"),
    row("Pods", 32),
    panel("Requests per second per pod", "Compare with the 1000/s local breaker; HPA should add pods well before it.",
          [(f"sum by (instance) (rate(http_server_request_duration_seconds_count{{{APP}}}[{RATE}]))", "{{instance}}")],
          0, 33, unit="reqps", thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 1000}]),
    panel("CPU cores per pod", "Process CPU time. Request is 1 core, limit 2.",
          [(f"sum by (instance) (rate(dotnet_process_cpu_time_seconds_total{{{JOB}}}[{RATE}]))", "{{instance}}")],
          12, 33, unit="short", thresholds=[{"color": "green", "value": None}, {"color": "orange", "value": 1}]),
    panel("Memory working set per pod", "Alert at 85% of the 512Mi limit.",
          [(f"max by (instance) (dotnet_process_memory_working_set_bytes{{{JOB}}})", "{{instance}}")],
          0, 41, unit="bytes", thresholds=[{"color": "green", "value": None}, {"color": "red", "value": 456340275}]),
    panel("Runtime pressure", "GC pause share and thread-pool queue; sustained queueing means the pod is CPU bound.",
          [(f"sum by (instance) (rate(dotnet_gc_pause_time_seconds_total{{{JOB}}}[{RATE}]))", "gc pause {{instance}}"),
           (f"sum by (instance) (dotnet_thread_pool_queue_length_total{{{JOB}}})", "queue {{instance}}")],
          12, 41),
    row("Dependencies", 49),
    panel("Authentication duration p99", "JWT validation time; rises when the IdP metadata endpoint is slow.",
          [(quantile(0.99, "aspnetcore_authentication_authenticate_duration_seconds_bucket", JOB), "p99")], 0, 50, unit="s"),
    panel("Shared quota (Redis)", "Redis call p99 and quota decisions taken by the local fallback while Redis is down.",
          [(quantile(0.99, "gateway_ratelimit_redis_duration_milliseconds_bucket", JOB), "redis p99 ms"),
           (f"sum(rate(gateway_ratelimit_distributed_fail_open_total{{{JOB}}}[{RATE}]))", "fail-open/s")],
          12, 50),
]

dashboard = {
    "title": "Core.Gateway",
    "uid": "core-gateway",
    "description": "Traffic, latency, load shedding, backends and pods of Core.Gateway. Thresholds match deploy/observability/prometheus-rules.yaml.",
    "tags": ["gateway", "yarp"],
    "timezone": "browser",
    "schemaVersion": 39,
    "refresh": "30s",
    "time": {"from": "now-3h", "to": "now"},
    "templating": {"list": [{"name": "datasource", "label": "Prometheus", "type": "datasource", "query": "prometheus"}]},
    "panels": panels,
}

if __name__ == "__main__":
    output = Path(__file__).with_name("grafana-dashboard.json")
    output.write_text(json.dumps(dashboard, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote {output} with {sum(1 for item in panels if item['type'] != 'row')} panels.")
