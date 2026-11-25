#!/usr/bin/env python3
"""Live nox-tts engine check (run on tp1). Two modes:

  direct:  /opt/nox-tts/venv/bin/python tts_engine_check.py direct     (imports /opt/nox-tts, no daemon)
  socket:  python3 tts_engine_check.py socket [--clone ref.wav]         (talks to /run/nox-tts/api.sock)

Synthesizes a short Persian or English line with every catalog voice and prints duration, wall time,
real-time factor and peak level, so silent/garbled output stands out. Exit 1 if any voice fails.
"""
from __future__ import annotations

import base64
import http.client
import io
import json
import socket
import sys
import time
import wave

FA = "سلام! این یک آزمایش کوتاه برای صدای فارسی است."
EN = "Hello! This is a short test of the offline voice engine."


def _peak(wav_bytes: bytes) -> tuple[float, float]:
    import array
    with wave.open(io.BytesIO(wav_bytes)) as w:
        n, sr = w.getnframes(), w.getframerate()
        a = array.array("h", w.readframes(n))
    return n / sr, (max((abs(x) for x in a), default=0) / 32768.0)


def direct() -> int:
    sys.path.insert(0, "/opt/nox-tts")
    import engines  # type: ignore
    import server  # type: ignore

    svc = server.TTSService(models_dir=engines.DEFAULT_MODELS_DIR)
    bad = 0
    for spec in engines.VOICE_CATALOG:
        if spec.engine == "pocket" or spec.id == "espeak:auto":
            continue
        if not engines.is_voice_installed(spec, engines.DEFAULT_MODELS_DIR):
            print(f"MISSING {spec.id}")
            bad += 1
            continue
        text = FA if spec.lang == "fa" else EN
        t0 = time.monotonic()
        try:
            res = svc.synthesize({"chat_id": 0, "text": text, "voice": spec.id, "format": "wav"})
            dur, peak = _peak(base64.b64decode(res["audio_b64"]))
            wall = time.monotonic() - t0
            used = res["segments"][0].get("voice")
            flag = "OK " if peak > 0.02 and dur > 0.5 and used == spec.id else "BAD"
            bad += flag == "BAD"
            print(f"{flag} {spec.id:22} used={used:22} dur={dur:5.2f}s wall={wall:5.2f}s rtf={wall / max(dur, 0.01):4.2f} peak={peak:.2f}")
        except Exception as exc:  # noqa: BLE001
            bad += 1
            print(f"ERR {spec.id:22} {type(exc).__name__}: {str(exc)[:200]}")
    print("status:", json.dumps(svc.status())[:400])
    svc.shutdown()
    return 1 if bad else 0


class _UnixConn(http.client.HTTPConnection):
    def __init__(self, path: str, timeout: float = 300.0):
        super().__init__("nox-tts", timeout=timeout)
        self._path = path

    def connect(self) -> None:
        self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
        self.sock.settimeout(self.timeout)
        self.sock.connect(self._path)


def _call(method: str, path: str, body: dict | None = None) -> tuple[int, dict]:
    conn = _UnixConn("/run/nox-tts/api.sock")
    data = json.dumps(body).encode() if body is not None else None
    conn.request(method, path, body=data, headers={"Content-Type": "application/json"} if data else {})
    res = conn.getresponse()
    return res.status, json.loads(res.read())


def via_socket(clone_wav: str | None) -> int:
    bad = 0
    st, health = _call("GET", "/healthz")
    print("healthz", st, health)
    st, voices = _call("GET", "/v1/voices?installed_only=1")
    ids = [v["id"] for v in voices.get("voices", [])]
    print("installed voices:", len(ids), ids)
    for vid, text in (("auto", FA), ("auto", EN), ("piper:fa-amir", FA), ("kokoro:af_heart", EN),
                      ("supertonic:F1", EN), ("kitten:f1", EN), ("flite:slt", EN), ("espeak:fa", FA),
                      ("pocket:alba", EN)):
        t0 = time.monotonic()
        st, res = _call("POST", "/v1/synthesize", {"chat_id": 1, "text": text, "voice": vid, "subtitles": "srt"})
        if st != 200:
            bad += 1
            print(f"ERR {vid:18} HTTP {st} {res}")
            continue
        dur, peak = _peak(base64.b64decode(res["audio_b64"]))
        print(f"OK  {vid:18} used={res['segments'][0].get('voice'):20} dur={dur:5.2f}s wall={time.monotonic() - t0:5.2f}s "
              f"peak={peak:.2f} cpu_ms={res.get('cpu_ms')} srt={'yes' if res.get('subtitles') else 'no'}")
    if clone_wav:
        ref = base64.b64encode(open(clone_wav, "rb").read()).decode()
        t0 = time.monotonic()
        st, res = _call("POST", "/v1/synthesize", {"chat_id": 1, "text": EN, "voice": "clone", "ref_audio_b64": ref})
        if st != 200:
            bad += 1
            print("ERR clone", st, res)
        else:
            dur, peak = _peak(base64.b64decode(res["audio_b64"]))
            print(f"OK  clone dur={dur:.2f}s wall={time.monotonic() - t0:.2f}s peak={peak:.2f}")
    st, status = _call("GET", "/v1/status")
    print("status", json.dumps(status)[:500])
    return 1 if bad else 0


if __name__ == "__main__":
    mode = sys.argv[1] if len(sys.argv) > 1 else "socket"
    clone = sys.argv[sys.argv.index("--clone") + 1] if "--clone" in sys.argv else None
    sys.exit(direct() if mode == "direct" else via_socket(clone))
