"""擷取既有 NLog 的有限 UTC 窗口；不啟動、停止或修改受測程序。"""
import argparse
import hashlib
import json
from datetime import datetime
from pathlib import Path
from statistics import mean


def timestamp(value):
    return datetime.fromisoformat(value)


def summarize(source, output, start, end):
    output.mkdir(parents=True, exist_ok=True)
    summaries = []
    for path in sorted(source.glob("*.jsonl")):
        # 活躍日誌可能有尚未完成的末行，只接受完整 JSON 行。
        selected, metrics, malformed = [], [], 0
        with path.open(encoding="utf-8-sig") as stream:
            for line in stream:
                try:
                    row = json.loads(line)
                except json.JSONDecodeError:
                    malformed += 1
                    continue
                if row["event"] == "started" or start <= timestamp(row["time"]) <= end:
                    selected.append(row)
                if row["event"] == "metrics" and start <= timestamp(row["time"]) <= end:
                    metrics.append(row)
        if len(metrics) < 2:
            continue
        raw = output / path.name
        raw.write_text("".join(json.dumps(r, ensure_ascii=False) + "\n" for r in selected), encoding="utf-8")
        first, last = metrics[0], metrics[-1]
        a, b = first["data"], last["data"]
        duration = (last["elapsedSeconds"] - first["elapsedSeconds"])
        cpu = b["cpuTotalMs"] - a["cpuTotalMs"]
        endpoints = {}
        for key, value in b["endpoints"].items():
            before = a["endpoints"][key]
            delta = {field: value[field] - before[field] for field in ("sent", "received", "errors", "closed")}
            count = value["measureReceived"] - before["measureReceived"]
            delta["scheduledLatencyMeanMs"] = ((value["measureScheduledLatencyMeanMs"] * value["measureReceived"] - before["measureScheduledLatencyMeanMs"] * before["measureReceived"]) / count) if count else None
            delta["activeMin"] = min(r["data"]["endpoints"][key]["active"] for r in metrics)
            delta["activeMax"] = max(r["data"]["endpoints"][key]["active"] for r in metrics)
            endpoints[key] = delta
        memory = {}
        for field in ("workingSetBytes", "privateBytes", "managedHeapBytes"):
            values = [r["data"][field] for r in metrics]
            invalid = [{"time": r["time"], "value": r["data"][field]} for r in metrics if r["data"][field] < 0]
            memory[field] = {"sampleMeanBytes": None if invalid else mean(values), "minBytes": min(values), "maxBytes": max(values), "validSamples": len(values) - len(invalid), "negativeSamples": len(invalid), "invalidSamples": invalid}
        summaries.append({"source": str(path.resolve()), "rawFile": raw.name, "rawSha256": hashlib.sha256(raw.read_bytes()).hexdigest(), "pid": first["pid"], "runId": first["runId"], "mode": first["mode"], "firstUtc": first["time"], "lastUtc": last["time"], "durationSeconds": duration, "samples": len(metrics), "phases": sorted(set(r["phase"] for r in metrics)), "cpuDeltaMs": cpu, "cpuOneCorePercent": cpu / (duration * 10), "cpuMachinePercent": cpu / (duration * 10 * b["cpuMachineDenominator"]), "cpuMachineDenominator": b["cpuMachineDenominator"], "memory": memory, "gcDelta": {str(g): b[f"gc{g}"] - a[f"gc{g}"] for g in range(3)}, "endpoints": endpoints, "errorEvents": sum(r["event"] == "error" for r in selected), "skippedMalformedLines": malformed})
    result = {"sourceType": "NLog process telemetry, not Rider profiler", "requestedStartUtc": start.isoformat(), "requestedEndUtc": end.isoformat(), "boundaryPolicy": "Per-process first and last complete metrics inside common UTC window; no interpolation; endpoints and CPU are cumulative differences.", "memoryPolicy": "Arithmetic sample mean, not time-weighted; any negative managed heap invalidates the managed summary.", "processes": summaries}
    (output / "summary.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return result


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--source", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--start", required=True, type=timestamp)
    parser.add_argument("--end", required=True, type=timestamp)
    args = parser.parse_args()
    print(json.dumps(summarize(args.source, args.output, args.start, args.end), ensure_ascii=False, indent=2))
