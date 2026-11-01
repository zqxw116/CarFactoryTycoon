#!/usr/bin/env python3
"""Tripo 3D OpenAPI (v3) CLI for the CarFactoryTycoon Unity asset pipeline.

Stdlib only (urllib). Docs: https://developers.tripo3d.com/en/docs

Subcommands:
  balance    Show available / frozen credits.
  multiview  Multiview (front/left/back/right) images -> 3D model.
  image      Single image -> 3D model.
  fetch      Download outputs of an existing task (resume).
  batch      Run several image/multiview jobs from a JSON file sequentially.

API key: env TRIPO_API_KEY, else file %USERPROFILE%\\.tripo\\api_key.txt.
The key is never printed, logged or written anywhere.
"""

import argparse
import json
import mimetypes
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

BASE_URL = "https://openapi.tripo3d.com/v3"
EP_BALANCE = "/account/balance"            # GET
EP_FILES = "/files"                        # POST multipart, field "file"
EP_IMAGE = "/generation/image-to-model"    # POST
EP_MULTIVIEW = "/generation/multiview-to-model"  # POST
EP_CONVERT = "/models/convert"             # POST
EP_TASK = "/tasks/{task_id}"               # GET

DEFAULT_MODEL = "v3.1-20260211"  # H3.1
KEY_FILE = os.path.join(os.path.expanduser("~"), ".tripo", "api_key.txt")
VIEW_ORDER = ("front", "left", "back", "right")
UPLOAD_EXTS = {".png", ".jpg", ".jpeg"}  # File Upload API: JPEG/PNG, <=20MB
MAX_IMAGE_BYTES = 20 * 1024 * 1024
FINAL_STATES = {"success", "failed", "cancelled", "banned", "expired"}

HTTP_TIMEOUT = 60
DOWNLOAD_TIMEOUT = 120
CHUNK = 1024 * 256


class TripoError(Exception):
    pass


# --------------------------------------------------------------------------- auth

def load_api_key():
    key = os.environ.get("TRIPO_API_KEY", "").strip()
    if key:
        return key
    if os.path.isfile(KEY_FILE):
        with open(KEY_FILE, "r", encoding="utf-8-sig") as f:
            key = f.read().strip()
        if key:
            return key
    raise TripoError(
        "API key not found. Set env TRIPO_API_KEY or create %s (contents: the key only)." % KEY_FILE)


def redact_headers(headers):
    return {k: ("Bearer ***REDACTED***" if k.lower() == "authorization" else v)
            for k, v in headers.items()}


# --------------------------------------------------------------------------- http

class Client:
    def __init__(self, api_key, debug=False):
        self._key = api_key
        self.debug = debug

    def _headers(self, extra=None):
        h = {"Authorization": "Bearer " + self._key, "Accept": "application/json"}
        if extra:
            h.update(extra)
        return h

    def _request(self, method, path, body=None, content_type=None, retries=5):
        url = BASE_URL + path
        extra = {"Content-Type": content_type} if content_type else None
        headers = self._headers(extra)
        if self.debug:
            print("[debug] %s %s headers=%s" % (method, url, redact_headers(headers)), file=sys.stderr)
        attempt = 0
        while True:
            req = urllib.request.Request(url, data=body, method=method, headers=headers)
            try:
                with urllib.request.urlopen(req, timeout=HTTP_TIMEOUT) as resp:
                    raw = resp.read()
                payload = json.loads(raw.decode("utf-8"))
                if payload.get("code", 0) != 0:
                    raise TripoError("API error: %s" % _brief(payload))
                return payload.get("data")
            except urllib.error.HTTPError as e:
                text = _safe_read(e)
                # Error Handling doc: retry 429 / 5xx with exponential backoff, max 5.
                if e.code in (429, 500, 502, 503, 504) and attempt < retries:
                    wait = 2 ** attempt
                    ra = e.headers.get("Retry-After") if e.headers else None
                    if ra and ra.isdigit():
                        wait = max(wait, int(ra))
                    print("  HTTP %d, retry in %ds..." % (e.code, wait), file=sys.stderr)
                    time.sleep(wait)
                    attempt += 1
                    continue
                raise TripoError("HTTP %d on %s %s: %s" % (e.code, method, path, text))
            except urllib.error.URLError as e:
                if attempt < retries:
                    wait = 2 ** attempt
                    print("  network error (%s), retry in %ds..." % (e.reason, wait), file=sys.stderr)
                    time.sleep(wait)
                    attempt += 1
                    continue
                raise TripoError("Network error on %s %s: %s" % (method, path, e.reason))

    def get(self, path):
        return self._request("GET", path)

    def post_json(self, path, obj):
        return self._request("POST", path, json.dumps(obj).encode("utf-8"), "application/json")

    def upload(self, file_path):
        boundary = "----tripo" + uuid.uuid4().hex
        fname = os.path.basename(file_path)
        ctype = mimetypes.guess_type(fname)[0] or "application/octet-stream"
        with open(file_path, "rb") as f:
            data = f.read()
        body = b"".join([
            ("--%s\r\n" % boundary).encode(),
            ('Content-Disposition: form-data; name="file"; filename="%s"\r\n' % fname).encode("utf-8"),
            ("Content-Type: %s\r\n\r\n" % ctype).encode(),
            data,
            ("\r\n--%s--\r\n" % boundary).encode(),
        ])
        res = self._request("POST", EP_FILES, body, "multipart/form-data; boundary=" + boundary)
        token = (res or {}).get("file_token")
        if not token:
            raise TripoError("Upload returned no file_token for %s" % fname)
        return token


def _safe_read(e):
    try:
        t = e.read().decode("utf-8", "replace")
    except Exception:
        return "(no body)"
    try:
        return _brief(json.loads(t))
    except Exception:
        return t[:500]


def _brief(payload):
    if isinstance(payload, dict):
        keys = ("code", "status", "message", "suggestion", "request_id")
        return json.dumps({k: payload[k] for k in keys if k in payload}, ensure_ascii=False)
    return str(payload)[:500]


# --------------------------------------------------------------------------- payloads

def validate_image(path):
    if not os.path.isfile(path):
        raise TripoError("Image not found: %s" % path)
    ext = os.path.splitext(path)[1].lower()
    if ext not in UPLOAD_EXTS:
        raise TripoError("Unsupported image type %s (upload accepts PNG/JPEG): %s" % (ext, path))
    if os.path.getsize(path) > MAX_IMAGE_BYTES:
        raise TripoError("Image larger than 20MB: %s" % path)


def build_common_params(o):
    """Generation params shared by image-to-model and multiview-to-model."""
    p = {"model": o["model_version"]}
    texture = bool(o["texture"]) or bool(o["pbr"])  # pbr forces texture=true
    p["texture"] = texture
    p["pbr"] = bool(o["pbr"])  # API default is true -> always send explicitly
    if o.get("face_limit") is not None:
        p["face_limit"] = int(o["face_limit"])
    if o.get("smart_low_poly"):
        p["smart_low_poly"] = True
    if o.get("quad"):
        p["quad"] = True
    if texture and o.get("texture_quality"):
        p["texture_quality"] = o["texture_quality"]
    optional = ("texture_version", "geometry_quality", "texture_alignment", "orientation",
                "model_seed", "texture_seed", "export_orientation", "compress")
    for k in optional:
        if o.get(k) is not None:
            p[k] = o[k]
    if o.get("auto_size"):
        p["auto_size"] = True
    if o.get("no_export_uv"):
        p["export_uv"] = False
    if o.get("extra"):
        p.update(o["extra"])
    validate_params(p)
    return p


def validate_params(p):
    fl = p.get("face_limit")
    quad = p.get("quad", False)
    if p.get("smart_low_poly") and fl is not None:
        hi = 10000 if quad else 20000
        if not (500 <= fl <= hi):
            raise TripoError("smart_low_poly face_limit must be 500..%d (got %d)" % (hi, fl))
    elif quad and fl is not None and fl > 150000:
        raise TripoError("quad face_limit max 150000 (got %d)" % fl)
    if p.get("texture_quality") == "fast" and p.get("texture_version") != "v3.5-20260815":
        raise TripoError("texture_quality=fast requires --texture-version v3.5-20260815")
    if p.get("generate_parts") and (p.get("texture") or p.get("pbr") or quad or p.get("smart_low_poly")):
        raise TripoError("generate_parts requires texture=false, pbr=false, no quad/smart_low_poly")


def estimate_credits(p, convert=None):
    """Estimate from https://developers.tripo3d.com/en/models/v3-1 (H3.1 table).
    Image/Multiview: no texture 20, standard 30, detailed 40, extreme(8K) 50.
    Add-ons: detailed geometry +20, quad +5, smart low-poly +10, parts +20.
    Convert Format: Basic 5 / Advanced 10 (pricing page; which tier applies is not documented)."""
    notes = []
    if not p.get("model", "").startswith("v3.1"):
        notes.append("table is for H3.1; other versions may differ")
    if not p.get("texture"):
        base = 20
    else:
        tq = p.get("texture_quality", "standard")
        base = {"standard": 30, "detailed": 40, "extreme": 50}.get(tq, 30)
        if tq == "fast":
            notes.append("fast texture price not listed; assumed 30")
    total = base
    parts = ["base %d" % base]
    for key, cost, label in (("smart_low_poly", 10, "smart_low_poly"), ("quad", 5, "quad"),
                             ("generate_parts", 20, "generate_parts")):
        if p.get(key):
            total += cost
            parts.append("%s +%d" % (label, cost))
    if p.get("geometry_quality") == "detailed":
        total += 20
        parts.append("detailed geometry +20")
    lo = hi = total
    if convert:
        lo, hi = total + 5, total + 10
        parts.append("convert +5~10")
    return lo, hi, ", ".join(parts), notes


def build_convert_payload(task_id, o):
    c = {"input": task_id, "format": o["convert"].upper()}
    if o.get("convert_texture_size"):
        c["texture_size"] = int(o["convert_texture_size"])
    if o.get("convert_texture_format"):
        c["texture_format"] = o["convert_texture_format"].upper()
    if o.get("pivot_bottom"):
        c["pivot_to_center_bottom"] = True
    if c["format"] == "FBX" and o.get("fbx_preset"):
        c["fbx_preset"] = o["fbx_preset"]
    if o.get("convert_face_limit"):
        c["face_limit"] = int(o["convert_face_limit"])
    return c


# --------------------------------------------------------------------------- task flow

def poll_task(client, task_id, interval, timeout):
    start = time.time()
    last = None
    while True:
        data = client.get(EP_TASK.format(task_id=urllib.parse.quote(task_id, safe="")))
        status = data.get("status")
        progress = data.get("progress")
        line = "  [%s] status=%s progress=%s%%" % (time.strftime("%H:%M:%S"), status, progress)
        if line.split("] ", 1)[1] != last:
            print(line, flush=True)
            last = line.split("] ", 1)[1]
        if status in FINAL_STATES:
            if status != "success":
                raise TripoError("Task %s ended with status=%s error_code=%s error_message=%s" % (
                    task_id, status, data.get("error_code"), data.get("error_message")))
            return data
        if time.time() - start > timeout:
            raise TripoError("Polling timeout after %ds. Resume later with: fetch --task-id %s"
                             % (timeout, task_id))
        time.sleep(interval)


_SAFE_NAME = re.compile(r"[^A-Za-z0-9_.-]+")


def _ext_from_url(url, default=".bin"):
    path = urllib.parse.urlparse(url).path
    ext = os.path.splitext(path)[1].lower()
    if re.fullmatch(r"\.[a-z0-9]{1,6}", ext or ""):
        return ext
    return default


def download(url, dest):
    """Stream untrusted bytes to disk. No auth header is sent to CDN URLs."""
    if urllib.parse.urlparse(url).scheme != "https":
        raise TripoError("Refusing non-https download URL")
    tmp = dest + ".part"
    req = urllib.request.Request(url, headers={"User-Agent": "tripo_gen/1.0"})
    with urllib.request.urlopen(req, timeout=DOWNLOAD_TIMEOUT) as resp, open(tmp, "wb") as f:
        total = 0
        while True:
            chunk = resp.read(CHUNK)
            if not chunk:
                break
            f.write(chunk)
            total += len(chunk)
    os.replace(tmp, dest)
    return total


def collect_output_urls(output):
    """Every http(s) URL in output (model_url, rendered_image_url, and any other *_url)."""
    found = []
    if not isinstance(output, dict):
        return found
    for k, v in output.items():
        if isinstance(v, str) and v.startswith("http"):
            found.append((k, v))
        elif isinstance(v, list):
            for i, item in enumerate(v):
                if isinstance(item, str) and item.startswith("http"):
                    found.append(("%s_%d" % (k, i), item))
    return found


def save_outputs(task, out_dir, name, suffix=""):
    os.makedirs(out_dir, exist_ok=True)
    safe = _SAFE_NAME.sub("_", name) + suffix
    files = []
    for key, url in collect_output_urls(task.get("output")):
        label = key[:-4] if key.endswith("_url") else key
        label = _SAFE_NAME.sub("_", label)
        dest = os.path.join(out_dir, "%s_%s%s" % (safe, label, _ext_from_url(url)))
        try:
            n = download(url, dest)
            print("  saved %s (%d bytes)" % (dest, n))
            files.append(dest)
        except Exception as e:  # keep going for other outputs
            print("  ! download failed for %s: %s" % (key, e), file=sys.stderr)
    json_path = os.path.join(out_dir, "%s_task.json" % safe)
    with open(json_path, "w", encoding="utf-8") as f:
        json.dump(task, f, ensure_ascii=False, indent=2)
    print("  saved %s" % json_path)
    if not files:
        print("  ! no output URLs downloaded", file=sys.stderr)
    return files


def run_generation(kind, payload, images, o, client):
    """images: list of (slot, path). slot None for single image."""
    if o["dry_run"]:
        shown = json.loads(json.dumps(payload))
        lo, hi, detail, notes = estimate_credits(payload, o.get("convert"))
        print("=== DRY RUN (nothing uploaded or sent) ===")
        for slot, path in images:
            print("upload  POST %s%s  file=%s" % (BASE_URL, EP_FILES, path))
        print("create  POST %s%s" % (BASE_URL, EP_MULTIVIEW if kind == "multiview" else EP_IMAGE))
        print(json.dumps(shown, ensure_ascii=False, indent=2))
        if o.get("convert"):
            print("convert POST %s%s" % (BASE_URL, EP_CONVERT))
            print(json.dumps(build_convert_payload("<generation task_id>", o), indent=2))
        est = "%d" % lo if lo == hi else "%d~%d" % (lo, hi)
        print("estimated credits: %s  (%s)" % (est, detail))
        for n in notes:
            print("  note: " + n)
        return None

    tokens = {}
    for slot, path in images:
        print("uploading %s ..." % os.path.basename(path), flush=True)
        tokens[slot] = client.upload(path)
    if kind == "multiview":
        payload["inputs"] = [{slot: tokens[slot]} for slot, _ in images]
    else:
        payload["input"] = tokens[None]

    ep = EP_MULTIVIEW if kind == "multiview" else EP_IMAGE
    data = client.post_json(ep, payload)
    task_id = data["task_id"]
    print("TASK_ID: %s   (resume: fetch --task-id %s --out \"%s\" --name %s)"
          % (task_id, task_id, o["out"], o["name"]), flush=True)
    task = poll_task(client, task_id, o["poll_interval"], o["timeout"])
    print("success. credits_consumed=%s" % task.get("credits_consumed"))
    save_outputs(task, o["out"], o["name"])

    if o.get("convert"):
        run_convert(client, task_id, o)
    return task_id


def run_convert(client, source_task_id, o):
    c = build_convert_payload(source_task_id, o)
    data = client.post_json(EP_CONVERT, c)
    ctid = data["task_id"]
    print("CONVERT TASK_ID: %s" % ctid, flush=True)
    task = poll_task(client, ctid, o["poll_interval"], o["timeout"])
    print("convert success. credits_consumed=%s" % task.get("credits_consumed"))
    save_outputs(task, o["out"], o["name"], suffix="_" + c["format"].lower())


# --------------------------------------------------------------------------- CLI

def add_gen_options(sp):
    sp.add_argument("--model-version", default=DEFAULT_MODEL, help="default %(default)s (H3.1)")
    sp.add_argument("--face-limit", type=int, default=15000,
                    help="max faces (default %(default)s; 0 = omit -> adaptive)")
    sp.add_argument("--smart-low-poly", action="store_true", help="smart_low_poly=true (+10 credits)")
    sp.add_argument("--quad", action="store_true", help="quad=true, forces FBX (+5 credits)")
    sp.add_argument("--texture", dest="texture", action="store_true", default=True,
                    help="texture=true (default)")
    sp.add_argument("--no-texture", dest="texture", action="store_false", help="texture=false")
    sp.add_argument("--pbr", action="store_true", default=False, help="pbr=true (default OFF)")
    sp.add_argument("--texture-quality", choices=["fast", "standard", "detailed", "extreme"])
    sp.add_argument("--texture-version", help="e.g. v3.5-20260815 (needed for fast/delight)")
    sp.add_argument("--geometry-quality", choices=["standard", "detailed"])
    sp.add_argument("--texture-alignment", choices=["original_image", "geometry"])
    sp.add_argument("--orientation", choices=["default", "align_image"])
    sp.add_argument("--export-orientation", choices=["+x", "-x", "+y", "-y"])
    sp.add_argument("--model-seed", type=int)
    sp.add_argument("--texture-seed", type=int)
    sp.add_argument("--auto-size", action="store_true")
    sp.add_argument("--no-export-uv", action="store_true")
    sp.add_argument("--params", help="JSON file of extra request fields merged into payload (override)")
    add_run_options(sp)
    sp.add_argument("--convert", choices=["fbx", "gltf", "obj", "usdz", "stl", "3mf"],
                    help="post-process via POST /v3/models/convert")
    sp.add_argument("--convert-texture-size", type=int, help="convert: texture_size (default API 4096)")
    sp.add_argument("--convert-texture-format", help="convert: texture_format, e.g. PNG")
    sp.add_argument("--pivot-bottom", action="store_true", help="convert: pivot_to_center_bottom=true")
    sp.add_argument("--fbx-preset", choices=["blender", "3dsmax", "mixamo", "bake_scale"])
    sp.add_argument("--convert-face-limit", type=int)
    sp.add_argument("--dry-run", action="store_true", help="print payloads + cost, send nothing")


def add_run_options(sp):
    sp.add_argument("--out", default="tripo_out", help="output directory")
    sp.add_argument("--name", default="tripo_model", help="file name prefix")
    sp.add_argument("--poll-interval", type=float, default=3.0)
    sp.add_argument("--timeout", type=int, default=1200, help="overall polling timeout seconds")


def opts_from_args(a):
    o = dict(vars(a))
    if o.get("face_limit") == 0:
        o["face_limit"] = None
    o["extra"] = None
    if o.get("params"):
        with open(o["params"], "r", encoding="utf-8") as f:
            o["extra"] = json.load(f)
    return o


def make_client(o):
    return Client(load_api_key(), debug=o.get("debug", False))


def cmd_balance(a):
    d = make_client(vars(a)).get(EP_BALANCE)
    print("available: %s   frozen: %s" % (d.get("balance"), d.get("frozen")))


def _multiview_images(o):
    images = []
    for slot in VIEW_ORDER:
        path = o.get(slot)
        if path:
            validate_image(path)
            images.append((slot, path))
    if not any(s == "front" for s, _ in images):
        raise TripoError("multiview: --front is required")
    if len(images) < 2:
        raise TripoError("multiview: at least 2 images required")
    return images


def do_multiview(o):
    images = _multiview_images(o)
    payload = build_common_params(o)
    # View-key format: [{"front": token}, {"back": token}, ...]; omitted views are simply left out.
    payload = dict({"inputs": [{s: "<file_token:%s>" % os.path.basename(p)} for s, p in images]}, **payload)
    client = None if o["dry_run"] else make_client(o)
    return run_generation("multiview", payload, images, o, client)


def do_image(o):
    validate_image(o["image"])
    payload = dict({"input": "<file_token:%s>" % os.path.basename(o["image"])}, **build_common_params(o))
    client = None if o["dry_run"] else make_client(o)
    return run_generation("image", payload, [(None, o["image"])], o, client)


def cmd_fetch(a):
    o = vars(a)
    client = make_client(o)
    task = client.get(EP_TASK.format(task_id=urllib.parse.quote(a.task_id, safe="")))
    st = task.get("status")
    print("task %s status=%s progress=%s" % (a.task_id, st, task.get("progress")))
    if st not in FINAL_STATES:
        task = poll_task(client, a.task_id, a.poll_interval, a.timeout)
    elif st != "success":
        raise TripoError("Task status=%s error_code=%s error_message=%s"
                         % (st, task.get("error_code"), task.get("error_message")))
    save_outputs(task, a.out, a.name)


def cmd_batch(a):
    """jobs JSON: {"defaults": {...}, "jobs": [{"name": "Door_L", "image": "a.png"},
    {"name": "Worker", "front": "f.png", "back": "b.png", "right": "r.png"}]}
    Keys use CLI option names with underscores (face_limit, smart_low_poly, out, convert, ...)."""
    with open(a.jobs, "r", encoding="utf-8") as f:
        spec = json.load(f)
    base_dir = os.path.dirname(os.path.abspath(a.jobs))
    parser = build_parser()
    failed = []
    for job in spec.get("jobs", []):
        merged = dict(spec.get("defaults", {}))
        merged.update(job)
        kind = "multiview" if "front" in merged else "image"
        ns = parser.parse_args([kind] + (["--front", "x"] if kind == "multiview" else ["--image", "x"]))
        o = opts_from_args(ns)
        o.update(merged)
        o["dry_run"] = a.dry_run or bool(merged.get("dry_run"))
        o["debug"] = a.debug
        for k in ("image",) + VIEW_ORDER:
            if o.get(k) and not os.path.isabs(o[k]):
                o[k] = os.path.join(base_dir, o[k])
        print("\n##### job %s (%s)" % (o["name"], kind))
        try:
            (do_multiview if kind == "multiview" else do_image)(o)
        except TripoError as e:
            print("  ! job failed: %s" % e, file=sys.stderr)
            failed.append(o["name"])
    if failed:
        raise TripoError("failed jobs: %s" % ", ".join(failed))


def build_parser():
    p = argparse.ArgumentParser(description="Tripo 3D OpenAPI v3 generator (Unity pipeline)")
    p.add_argument("--debug", action="store_true", help="print requests (Authorization redacted)")
    sub = p.add_subparsers(dest="cmd", required=True)

    sub.add_parser("balance", help="show credits (GET /v3/account/balance)")

    mv = sub.add_parser("multiview", help="multiview images -> model")
    for v in VIEW_ORDER:
        mv.add_argument("--" + v, help="%s view image (PNG/JPEG)" % v)
    add_gen_options(mv)

    im = sub.add_parser("image", help="single image -> model")
    im.add_argument("--image", required=True)
    add_gen_options(im)

    fe = sub.add_parser("fetch", help="download outputs of an existing task")
    fe.add_argument("--task-id", required=True)
    add_run_options(fe)

    ba = sub.add_parser("batch", help="run jobs from a JSON file")
    ba.add_argument("--jobs", required=True)
    ba.add_argument("--dry-run", action="store_true")
    return p


def main(argv=None):
    a = build_parser().parse_args(argv)
    try:
        if a.cmd == "balance":
            cmd_balance(a)
        elif a.cmd == "multiview":
            do_multiview(opts_from_args(a))
        elif a.cmd == "image":
            do_image(opts_from_args(a))
        elif a.cmd == "fetch":
            cmd_fetch(a)
        elif a.cmd == "batch":
            cmd_batch(a)
    except TripoError as e:
        print("ERROR: %s" % e, file=sys.stderr)
        return 1
    except KeyboardInterrupt:
        print("\ninterrupted", file=sys.stderr)
        return 130
    return 0


if __name__ == "__main__":
    sys.exit(main())
