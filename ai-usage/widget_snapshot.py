#!/usr/bin/env python3
import collections, concurrent.futures, datetime as dt, gzip, itertools, json, math, os, queue, re, shutil, sqlite3, subprocess, sys, tempfile, threading, time
if os.name == "nt":
    import msvcrt
    class fcntl:
        LOCK_EX, LOCK_NB = 1, 2
        @staticmethod
        def flock(file, flags):
            file.seek(0)
            try: msvcrt.locking(file.fileno(), msvcrt.LK_NBLCK if flags & fcntl.LOCK_NB else msvcrt.LK_LOCK, 1)
            except OSError as exc: raise BlockingIOError(str(exc)) from exc
else:
    import fcntl
from pathlib import Path
from statistics import median
from urllib import error, parse, request
CACHE = Path(os.environ.get("XDG_CACHE_HOME", "~/.cache")).expanduser() / "ai-usage"
HISTORY_CACHE, ERROR_CACHE, TOKEN_CACHE, CODEX_CACHE, AGY_CACHE = (CACHE / name for name in ("usage-history.json", "error-history.json", "token-stats.json", "codex-usage.json", "agy-usage.json"))
PROVIDERS = ("claude", "codex", "kimi", "grok", "agy")
TOKEN_WINDOWS = (("lifetime", None), ("30d", 30 * 86400), ("7d", 7 * 86400), ("24h", 86400), ("1h", 3600))
COLORS = {"ok": "#27ae60", "near": "#fdbc4b", "under": "#3daee9"}
TARGET_USED, FULL_USED = 80.0, 99.5
NETWORK_ERRORS = (OSError, error.URLError, TimeoutError, json.JSONDecodeError)
OPENAI_PRICES = {
    "gpt-5.6-sol": (5.0, 0.5, 30.0), "gpt-5.6-terra": (2.5, 0.25, 15.0), "gpt-5.6-luna": (1.0, 0.1, 6.0),
    "gpt-5-codex": (1.25, 0.125, 10.0), "gpt-5.5": (10.0, 1.0, 45.0), "gpt-6-astra": (10.0, 1.0, 50.0),
    "gpt-5.4-mini": (0.75, 0.075, 4.5), "gpt-5.3-codex": (1.75, 0.175, 14.0),
}
CLAUDE_PRICES = {
    **dict.fromkeys(("claude-opus-5", "claude-opus-4-8", "claude-opus-4-7", "claude-opus-4-6", "claude-opus-4-5"), (5.0, 6.25, 10.0, 0.5, 25.0)),
    "claude-opus-4-1": (15.0, 18.75, 30.0, 1.5, 75.0), "claude-opus-4": (15.0, 18.75, 30.0, 1.5, 75.0),
    "claude-fable-5": (10.0, 12.5, 20.0, 1.0, 50.0), "claude-fable-5-1": (10.0, 12.5, 20.0, 0.25, 50.0),
    "claude-sonnet-5": (2.0, 2.5, 4.0, 0.2, 10.0),
    "claude-sonnet-4-6": (3.0, 3.75, 6.0, 0.3, 15.0),
    "claude-haiku-4-5-20251001": (1.0, 1.25, 2.0, 0.1, 5.0),
    "claude-ocx-native--gpt-5.6-sol": (5.0, 5.0, 5.0, 0.5, 30.0),
}
KIMI_PRICES = {"kimi-code/kimi-for-coding": (0.95, 0.19, 4.0), "kimi-k2.7-code": (0.95, 0.19, 4.0), "kimi-k2.7-code-highspeed": (1.9, 0.38, 8.0)}
AGY_PRICES = {
    "gemini-3.7-flash": (0.75, 0.075, 3.75), "gemini-3.6-flash": (0.75, 0.075, 3.75),
    "gemini-3.5-flash": (1.5, 0.15, 9.0), "gemini-3.1-pro": (2.0, 0.2, 12.0),
    "gemini-3.1-pro (>200k)": (4.0, 0.4, 18.0),
}
KIMI_CLIENT_ID, KIMI_BASE_URL, KIMI_AUTH_HOST = "17e5f671-d194-4dfb-9706-5516cb48c098", "https://api.kimi.com/coding/v1", "https://auth.kimi.com"
CLAUDE_CLIENT_ID, CLAUDE_API_URL, CLAUDE_TOKEN_URL = "9d1c250a-e61b-44d9-88ed-5944d1962f5e", "https://api.anthropic.com", "https://platform.claude.com/v1/oauth/token"
GROK_BASE_URL = "https://cli-chat-proxy.grok.com/v1"
AGY_RPC = "/exa.language_server_pb.LanguageServerService/"
RESET_API = "https://codex-reset.com/api/"
def now(): return dt.datetime.now().astimezone()
def notice(provider, reason): return f"{now():%b %d %H:%M} - {provider}: {reason}"
def failure(provider, value):
    code, text = getattr(value, "code", None), str(value).lower()
    reason = "unauthorized (401)" if code == 401 or "401" in text else "forbidden (403)" if code == 403 or "403" in text else "usage lookup timed out" if isinstance(value, TimeoutError) or "timed out" in text else "network unavailable" if any(word in text for word in ("network", "connect", "resolve", "route", "dns")) else "usage lookup failed"
    return notice(provider, reason)
def number(value, integer=False):
    try: return int(value or 0) if integer else float(value)
    except (TypeError, ValueError): return 0 if integer else None
def dig(source, *keys):
    for key in keys:
        if not isinstance(source, dict): return None
        source = source.get(key)
    return source
def load(path):
    try: return json.loads(path.read_text(encoding="utf-8"))
    except (FileNotFoundError, json.JSONDecodeError): return {}
def save(path, data, mode=None):
    # Windows turns 0o700 into an owner-only ACL; an elevated run makes Administrators the owner, locking out the sign-in widget.
    path.parent.mkdir(parents=True, exist_ok=True, mode=0o700 if mode and os.name != "nt" else 0o777)
    tmp = path.with_suffix(".tmp")
    tmp.write_text(json.dumps(data, separators=(",", ":")))
    if mode: os.chmod(tmp, mode)
    tmp.replace(path)
def moment(value):
    stamp = number(value)
    try:
        if stamp is not None:
            stamp = stamp / 1000 if stamp > 10**12 else stamp
            return dt.datetime.fromtimestamp(stamp, dt.timezone.utc).astimezone()
        if isinstance(value, str): return dt.datetime.fromisoformat(value.replace("Z", "+00:00")).astimezone()
    except (OSError, OverflowError, ValueError): pass
    return None
def http(url, headers=None, body=None, timeout=8):
    req = request.Request(url, data=body, headers=headers or {})
    with request.urlopen(req, timeout=timeout) as response:
        value = json.loads(response.read().decode())
    return value if isinstance(value, dict) else {}
def duration(seconds):
    minutes = max(1, math.ceil(seconds / 60))
    if minutes < 90: return f"{minutes}m"
    hours = (minutes + 30) // 60
    if hours < 36: return f"{hours}h"
    days, extra = divmod(hours, 24)
    return f"{days}d {extra}h" if extra and days < 3 else f"{days}d"
def reset_fields(epoch):
    reset = moment(epoch)
    if not reset: return {"reset": None, "reset_label": "--", "days": "?"}
    ref = now()
    seconds = max(0, int((reset - ref).total_seconds()))
    if reset.date() == ref.date():
        label = reset.strftime("%H:%M")
    elif reset.date() == (ref + dt.timedelta(days=1)).date():
        label = "tomorrow " + reset.strftime("%H:%M")
    elif seconds < 7 * 86400:
        label = reset.strftime("%a %H:%M")
    else:
        label = reset.strftime("%b %d %H:%M")
    return {"reset": reset.timestamp(), "reset_label": f"Resets {label}", "days": str(seconds // 86400)}
def quota_health(used, reset, minutes):
    if used is None: return {"pace": "--", "color": ""}
    projected = raw_projected = early = None
    if reset is not None and minutes:
        left = max(0.0, reset - now().timestamp())
        window = minutes * 60
        elapsed = max(0.0, 1.0 - min(1.0, left / window))
        raw_projected = used if elapsed <= 0 else used / elapsed
        projected = min(100.0, raw_projected)
        if raw_projected > 100 and used > 0: early = left if used >= 100 else left - (100 - used) * window * elapsed / used
    if raw_projected is None:
        pace = "Forecast pending"
    elif early is not None and early > 0:
        pace = f"Limit {duration(early)} early"
    else:
        pace = f"Expected {round(projected if projected is not None else used)}%"
    if early is not None and early > 0:
        state = "near"
    elif raw_projected is not None:
        state = "ok" if raw_projected >= TARGET_USED else "near" if raw_projected >= TARGET_USED * 0.8 else "under"
    else:
        state = "ok" if used >= TARGET_USED else "near" if used >= TARGET_USED * 0.8 else "under"
    return {"pace": pace, "color": COLORS[state]}
def quota(raw=None, minutes=None, captured=None):
    if not isinstance(raw, dict): raw = {}
    used = next((number(raw.get(key)) for key in ("used_percent", "used_percentage", "usedPercent", "utilization") if raw.get(key) is not None), None)
    limit, absolute = number(raw.get("limit")), number(raw.get("used"))
    remaining = number(raw.get("remaining"))
    if absolute is None and limit is not None and remaining is not None: absolute = limit - remaining
    if used is None and absolute is not None and limit: used = absolute * 100 / limit
    used = None if used is None else min(100.0, max(0.0, used))
    reset = next((raw.get(key) for key in ("resets_at", "resetsAt", "reset_at", "resetAt", "reset_time", "resetTime") if raw.get(key) is not None), None)
    reset = moment(reset)
    if not reset:
        relative = next((number(raw.get(key)) for key in ("resets_in_seconds", "reset_in", "resetIn", "ttl") if raw.get(key) is not None), None)
        if relative is not None: reset = (captured or now()) + dt.timedelta(seconds=relative)
    if minutes is None: minutes = number(raw.get("window_minutes", raw.get("windowDurationMins")), True) or None
    epoch = reset.timestamp() if reset else None
    return {"used": used, **reset_fields(epoch), **quota_health(used, epoch, minutes)}
def window_minutes(raw):
    if not isinstance(raw, dict): return None
    window = raw.get("window") if isinstance(raw.get("window"), dict) else raw
    value = number(window.get("duration"))
    unit = str(window.get("timeUnit", "")).upper()
    if value is None: return None
    if "MINUTE" in unit: return round(value)
    if "HOUR" in unit: return round(value * 60)
    if "DAY" in unit: return round(value * 1440)
    return round(value / 60 if value > 1000 else value)
def blank_provider(extra=None):
    data = {"available": False, "current": quota(), "weekly": quota()}
    if extra:
        data.update(extra)
    return data
def kimi_home(): return Path(os.environ.get("KIMI_CODE_HOME", "~/.kimi-code")).expanduser()
def kimi_headers(home):
    version = "0.27.0"
    for path in (home / "updates/latest.json", home / "updates/install.json"):
        data = load(path)
        version = data.get("latest") or data.get("version") or version
    headers = {"X-Msh-Platform": "kimi_code_cli", "X-Msh-Version": version}
    try:
        device = (home / "device_id").read_text().strip()
        if device: headers["X-Msh-Device-Id"] = device
    except OSError: pass
    return headers
def kimi_token(home, refresh=False):
    path = home / "credentials/kimi-code.json"
    credentials = load(path)
    token = credentials.get("access_token")
    expires = number(credentials.get("expires_at"))
    expires = expires / 1000 if expires and expires > 10**12 else expires
    if not refresh and token and (expires is None or expires - now().timestamp() > 60): return token
    refresh_token = credentials.get("refresh_token")
    if not refresh_token: return token
    body = parse.urlencode({"client_id": KIMI_CLIENT_ID, "grant_type": "refresh_token", "refresh_token": refresh_token}).encode()
    try:
        payload = http(
            os.environ.get("KIMI_CODE_OAUTH_HOST", os.environ.get("KIMI_OAUTH_HOST", KIMI_AUTH_HOST)).rstrip("/") + "/api/oauth/token",
            {"Accept": "application/json", "Content-Type": "application/x-www-form-urlencoded", **kimi_headers(home)}, body,
        )
    except NETWORK_ERRORS: return token
    token = payload.get("access_token")
    if not token: return None
    credentials.update(payload)
    if number(payload.get("expires_in")): credentials["expires_at"] = int(now().timestamp() + number(payload["expires_in"]))
    save(path, credentials, 0o600)
    return token
def kimi():
    home = kimi_home()
    try: token = kimi_token(home)
    except NETWORK_ERRORS: return blank_provider()
    if not token: return blank_provider()
    url = os.environ.get("KIMI_CODE_BASE_URL", KIMI_BASE_URL).rstrip("/") + "/usages"
    try:
        try:
            payload = http(url, {"Authorization": f"Bearer {token}", "Accept": "application/json"})
        except error.HTTPError as exc:
            if exc.code != 401 or not (token := kimi_token(home, True)):
                raise
            payload = http(url, {"Authorization": f"Bearer {token}", "Accept": "application/json"})
    except NETWORK_ERRORS: return blank_provider()
    weekly = payload.get("usage") if isinstance(payload.get("usage"), dict) else None
    limits = payload.get("limits") if isinstance(payload.get("limits"), list) else []
    item = next((item for item in limits if window_minutes(item) == 300), None)
    current = item.get("detail") if isinstance(item, dict) and isinstance(item.get("detail"), dict) else item
    return {"available": bool(current or weekly), "current": quota(current, window_minutes(item) or 300), "weekly": quota(weekly, 10080)}
def grok_home(): return Path(os.environ.get("GROK_HOME", "~/.grok")).expanduser()
def grok_credentials(auth):
    return next(((scope, value) for scope, value in auth.items() if isinstance(value, dict) and (value.get("key") or value.get("refresh_token"))), (None, {}))
def grok_token(home, refresh=False):
    path = home / "auth.json"
    auth = load(path); scope, credentials = grok_credentials(auth)
    token, expires = credentials.get("key"), moment(credentials.get("expires_at"))
    if not refresh and token and (not expires or (expires - now()).total_seconds() > 60): return token
    if not scope or not credentials.get("refresh_token") or not credentials.get("oidc_client_id"): return token
    with Path(str(path) + ".lock").open("a+") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        auth = load(path); scope, credentials = grok_credentials(auth)
        token, expires = credentials.get("key"), moment(credentials.get("expires_at"))
        if not refresh and token and (not expires or (expires - now()).total_seconds() > 60): return token
        if not scope or not credentials.get("refresh_token") or not credentials.get("oidc_client_id"): return token
        body = parse.urlencode({"client_id": credentials["oidc_client_id"], "grant_type": "refresh_token", "refresh_token": credentials["refresh_token"]}).encode()
        payload = http(credentials.get("oidc_issuer", "https://auth.x.ai").rstrip("/") + "/oauth2/token", {"Accept": "application/json", "Content-Type": "application/x-www-form-urlencoded"}, body)
        token = payload.get("access_token")
        if not token: return None
        credentials.update({"key": token, "refresh_token": payload.get("refresh_token", credentials["refresh_token"])})
        if number(payload.get("expires_in")): credentials["expires_at"] = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(seconds=number(payload["expires_in"]))).isoformat().replace("+00:00", "Z")
        auth[scope] = credentials; save(path, auth, 0o600)
    return token
def grok():
    home = grok_home()
    try:
        token = grok_token(home)
        if not token: return blank_provider({"error": notice("Grok", "sign in required")})
        url = os.environ.get("CLI_CHAT_PROXY_BASE_URL", GROK_BASE_URL).rstrip("/") + "/billing?format=credits"
        headers = {"Authorization": f"Bearer {token}", "Accept": "application/json", "x-grok-client-mode": "interactive"}
        try: payload = http(url, headers)
        except error.HTTPError as exc:
            if exc.code != 401 or not (token := grok_token(home, True)): raise
            headers["Authorization"] = f"Bearer {token}"; payload = http(url, headers)
    except NETWORK_ERRORS as exc: return blank_provider({"error": failure("Grok", exc)})
    config = payload.get("config") if isinstance(payload.get("config"), dict) else {}
    period = config.get("currentPeriod") if isinstance(config.get("currentPeriod"), dict) else {}
    reset = period.get("end") or config.get("billingPeriodEnd")
    data = {"available": bool(reset), "current": quota(), "weekly": quota({"used_percent": config.get("creditUsagePercent", 0), "resets_at": reset}, 10080)}
    if not reset: data["error"] = notice("Grok", "usage data missing")
    return data
def agy_home(): return Path(os.environ.get("GEMINI_CLI_HOME", "~/.gemini/antigravity-cli")).expanduser()
def agy_ports():
    ports = set()
    for process in Path("/proc").glob("[0-9]*"):
        try:
            if (process / "comm").read_text().strip() != "agy": continue
            sockets = set()
            for fd in (process / "fd").iterdir():
                try: target = os.readlink(fd)
                except OSError: continue
                if target.startswith("socket:["): sockets.add(target[8:-1])
            for table in (process / "net/tcp", process / "net/tcp6"):
                for line in table.read_text().splitlines()[1:]:
                    columns = line.split()
                    if columns[3] == "0A" and columns[9] in sockets: ports.add(int(columns[1].rsplit(":", 1)[1], 16))
        except (OSError, ValueError): continue
    return sorted(ports)
def agy_rpc(port, method):
    body = json.dumps({"metadata": {"ideName": "antigravity", "extensionName": "antigravity", "locale": "en"}}).encode()
    return http(f"http://127.0.0.1:{port}{AGY_RPC}{method}", {"Content-Type": "application/json", "Connect-Protocol-Version": "1"}, body, 2)
def agy_lookup():
    if not (ports := agy_ports()): raise RuntimeError("not running")
    last_error = None
    for port in ports:
        try:
            summary, status = agy_rpc(port, "RetrieveUserQuotaSummary"), agy_rpc(port, "GetUserStatus")
            groups, user = (summary.get("response") or {}).get("groups"), status.get("userStatus")
            if not isinstance(groups, list): raise RuntimeError("quota response missing groups")
            if not isinstance(user, dict) or not user.get("email"): raise RuntimeError("account response missing email")
            return groups, user["email"]
        except (OSError, error.URLError, TimeoutError, json.JSONDecodeError, RuntimeError) as exc:
            if isinstance(exc, RuntimeError) or not isinstance(last_error, RuntimeError): last_error = exc
    if last_error: raise last_error
    raise RuntimeError("local quota service unavailable")
def agy_pool(group):
    title = str(group.get("displayName", "")); lower = title.lower()
    family = "gemini" if "gemini" in lower else "third_party" if "claude" in lower or "gpt" in lower else None
    if not family: return None
    values = {"title": "Gemini" if family == "gemini" else "Claude/GPT", "family": family}
    for bucket in group.get("buckets", []):
        window = str(bucket.get("window", "")).lower().replace("-", "_")
        key = "current" if window in ("5h", "five_hour") else "weekly" if window in ("week", "weekly") else None
        if not key: continue
        remaining, reset = number(bucket.get("remainingFraction")), bucket.get("resetTime")
        if remaining is None or not 0 <= remaining <= 1 or not reset: raise RuntimeError(f"{title} {window} quota is incomplete")
        values[key] = quota({"used_percent": (1 - remaining) * 100, "resets_at": reset}, 300 if key == "current" else 10080)
    if not any(key in values for key in ("current", "weekly")): raise RuntimeError(f"{title} has no supported quota windows")
    return values
def agy_provider(groups, model, account=None, stale_at=None, error_text=""):
    pools = [pool for group in groups or [] if isinstance(group, dict) and (pool := agy_pool(group))]
    model_name = str(model or ""); lower = model_name.lower()
    family = "third_party" if lower.startswith(("claude", "gpt")) else "gemini" if lower.startswith("gemini") else None
    primary = next((pool for pool in pools if pool["family"] == family), {})
    if stale_at:
        for pool in pools:
            for key in ("current", "weekly"):
                if pool.get(key, {}).get("reset") and pool[key]["reset"] <= time.time(): pool[key] = quota()
    rows = []
    for pool in sorted(pools, key=lambda value: value["family"] != family):
        for key, label in (("current", "5h"), ("weekly", "Week")):
            if pool.get(key, {}).get("used") is not None: rows.append({"title": f"{pool['title']} {label}", "data": pool[key]})
    data = {"available": any(primary.get(key, {}).get("used") is not None for key in ("current", "weekly")), "current": primary.get("current", quota()), "weekly": primary.get("weekly", quota()), "rows": rows}
    if account and family: data["_account"] = f"{account}:{family}"
    if stale_at:
        age = max(0, time.time() - stale_at)
        data["stale"] = "Last confirmed just now" if age < 60 else f"Last confirmed {duration(age)} ago"
    if not family: error_text = error_text or notice("Agy", "selected model missing")
    elif not primary: error_text = error_text or notice("Agy", "selected model quota group missing")
    if error_text: data["error"] = error_text
    return data
def agy():
    home, cached = agy_home(), load(AGY_CACHE)
    try:
        groups, account = agy_lookup()
        model = load(home / "settings.json").get("model")
        if not isinstance(model, str) or not model: raise RuntimeError("selected model missing")
        data = agy_provider(groups, model, account)
        cached = {"account": account, "observed_at": time.time(), "groups": groups, "model": model}
        try: save(AGY_CACHE, cached, 0o600)
        except OSError as exc: data["error"] = notice("Agy", f"cache write failed ({exc.errno})")
        return data
    except (OSError, error.URLError, TimeoutError, json.JSONDecodeError, RuntimeError) as exc:
        reason = str(exc) if isinstance(exc, RuntimeError) else failure("Agy", exc).split(" - ", 1)[-1]
        return agy_provider(cached.get("groups"), cached.get("model"), cached.get("account"), number(cached.get("observed_at")), notice("Agy", reason))
def rpc(process, payload, timeout=5):
    if not process.stdin or not process.stdout: return None
    process.stdin.write(json.dumps(payload, separators=(",", ":")) + "\n")
    process.stdin.flush()
    if not hasattr(process, "_rpc_lines"):
        process._rpc_lines = queue.Queue()
        def read_lines():
            try:
                for line in process.stdout: process._rpc_lines.put(line)
            finally: process._rpc_lines.put(None)
        threading.Thread(target=read_lines, daemon=True).start()
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        try: line = process._rpc_lines.get(timeout=max(0, deadline - time.monotonic()))
        except queue.Empty: break
        if not line: break
        try:
            response = json.loads(line)
        except json.JSONDecodeError: continue
        if response.get("id") == payload.get("id"):
            if response.get("error"): raise RuntimeError(json.dumps(response["error"]))
            result = response.get("result")
            return result if isinstance(result, dict) else None
    raise TimeoutError(f"{payload.get('method', 'request')} timed out after {timeout}s")
def short_time(value):
    value, ref = moment(value), now()
    if not value: return ""
    delta = (value.date() - ref.date()).days
    if delta == 0: return "today " + value.strftime("%H:%M")
    if delta == 1: return "tomorrow " + value.strftime("%H:%M")
    if delta == -1: return "yesterday " + value.strftime("%H:%M")
    return value.strftime("%a %H:%M") if abs(delta) < 7 else value.strftime("%b %d %H:%M")
def reset_info():
    headers = {"Accept": "application/json", "User-Agent": "KDE-AI-Usage-Widget/1"}
    result = {}
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        timeline = pool.submit(http, RESET_API + "timeline?group=reset", headers, None, 3)
        prediction = pool.submit(http, RESET_API + "forecast", headers, None, 3)
    try:
        events = timeline.result().get("events", [])
        events = events if isinstance(events, list) else []
        labels = [short_time(item.get("announced_at")) for item in events if isinstance(item, dict)][:3]
        if labels: result["past"] = " | ".join(filter(None, labels))
    except NETWORK_ERRORS: pass
    try:
        forecast = prediction.result()
        updated = moment(forecast.get("updated_at"))
        if not updated or abs((now() - updated).total_seconds()) > 7200: return result
        signal = forecast.get("official_signal")
        if signal:
            window = signal.get("official_window", signal.get("window", {})) if isinstance(signal, dict) else {}
            label = window.get("label") if isinstance(window, dict) else None
            result["next"] = str(label).capitalize() + " (announced)" if label else "Announced"
        else:
            odds = forecast.get("probabilities", {})
            day, two_days = number(odds.get("rounded_24h")), number(odds.get("rounded_48h"))
            if day is not None and two_days is not None: result["next"] = f"24h ~{round(day)}%, 48h ~{round(two_days)}%"
    except NETWORK_ERRORS: pass
    return result
def banked(payload):
    if not isinstance(payload, dict): return ""
    raw_credits = payload.get("credits")
    credits = [item for item in raw_credits if isinstance(item, dict) and item.get("status") == "available"] if isinstance(raw_credits, list) else []
    count = number(payload.get("availableCount"))
    count = round(count) if count is not None else len(credits)
    expiries = sorted(value for item in credits if (value := number(item.get("expiresAt"))) and value > now().timestamp())
    return f"{count} | {'expires' if count == 1 else 'next expiry'} {short_time(expiries[0])}" if expiries else str(count)
def codex_windows(limits):
    values = limits.values() if isinstance(limits, dict) else limits or []
    return {str(minutes): item for item in values if isinstance(item, dict) and (minutes := round(number(item.get("windowDurationMins", item.get("window_minutes"))) or 0))}
def codex_attempt(binary, deadline):
    started, process, stage, result, issue = time.monotonic(), None, "start", None, None
    stderr = tempfile.TemporaryFile(mode="w+t", encoding="utf-8", errors="replace")
    try:
        process = subprocess.Popen([binary, "app-server", "--stdio"], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=stderr, text=True, encoding="utf-8", bufsize=1, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
        stage = "initialize"
        left = deadline - time.monotonic()
        if left <= 0: raise TimeoutError("lookup budget exhausted")
        initialized = rpc(process, {"method": "initialize", "id": 1, "params": {"clientInfo": {"name": "kde_ai_usage", "title": "KDE AI Usage", "version": "1"}}}, min(2, left))
        if initialized is None or not process.stdin: raise RuntimeError("empty initialize response")
        process.stdin.write('{"method":"initialized"}\n'); process.stdin.flush()
        stage = "rate limits"
        left = deadline - time.monotonic()
        if left <= 0: raise TimeoutError("lookup budget exhausted")
        result = rpc(process, {"method": "account/rateLimits/read", "id": 2}, min(6, left))
        if result is None: raise RuntimeError("empty response")
    except (BrokenPipeError, OSError, subprocess.SubprocessError, RuntimeError, TimeoutError) as exc:
        issue = {"stage": stage, "timeout": isinstance(exc, TimeoutError), "message": str(exc)}
    finally:
        if process and process.poll() is None:
            process.terminate()
            try: process.wait(timeout=0.5)
            except subprocess.TimeoutExpired:
                process.kill(); process.wait()
        stderr.flush(); stderr.seek(0)
        detail = " ".join(stderr.read().split()).replace(str(Path.home()), "~")
        detail = re.sub(r"\x1b\[[0-?]*[ -/]*[@-~]", "", detail)
        detail = re.sub(r"(?i)(bearer\s+|(?:access|refresh|id)[ _-]?token\s*[:=]\s*)\S+", r"\1<redacted>", detail)
        detail = re.sub(r"(?i)([?&](?:token|key|code)=)[^&\s]+", r"\1<redacted>", detail)[-180:]
        stderr.close()
    if issue: issue.update(stderr=detail, elapsed_ms=round((time.monotonic() - started) * 1000))
    return result, issue
def codex_local_usage(home, after):
    paths = []
    try:
        for path in (home / "sessions").rglob("*.jsonl"):
            try: paths.append((path.stat().st_mtime, path))
            except OSError: pass
    except OSError: return after, None
    latest, found = after, None
    for modified, path in sorted(paths, reverse=True)[:24]:
        if modified <= after: continue
        try:
            with path.open("rb") as lines:
                lines.seek(max(0, path.stat().st_size - 1024 * 1024))
                if lines.tell(): lines.readline()
                for line in lines:
                    try: item = json.loads(line)
                    except (json.JSONDecodeError, UnicodeDecodeError): continue
                    if not isinstance(item, dict) or not isinstance(item.get("payload"), dict): continue
                    payload = item["payload"]; limits = payload.get("rate_limits")
                    if item.get("type") != "event_msg" or payload.get("type") != "token_count" or not isinstance(limits, dict) or limits.get("limit_id") != "codex": continue
                    captured = moment(item.get("timestamp")); windows = codex_windows(limits.get(key) for key in ("primary", "secondary"))
                    if captured and latest < captured.timestamp() <= time.time() + 300 and windows:
                        latest, found = captured.timestamp(), windows
        except OSError: continue
    return latest, found
def codex_provider(windows, credits=None, stale_at=None, error_text=""):
    windows = {number(key, True): value for key, value in (windows or {}).items() if isinstance(value, dict)}
    current, weekly = quota(windows.get(300), 300), quota(windows.get(10080), 10080)
    if stale_at:
        if current.get("reset") and current["reset"] <= time.time(): current = quota()
        if weekly.get("reset") and weekly["reset"] <= time.time(): weekly = quota()
    data = {"available": current.get("used") is not None or weekly.get("used") is not None, "current": current, "weekly": weekly}
    if credit := banked(credits): data["_banked"] = credit
    if stale_at:
        age = max(0, time.time() - stale_at)
        data["stale"] = "Last confirmed just now" if age < 60 else f"Last confirmed {duration(age)} ago"
    if error_text: data["error"] = error_text
    return data
def codex_error(issue, attempts, elapsed):
    text = (issue.get("message", "") + " " + issue.get("stderr", "")).lower()
    reason = f"{issue['stage']} timed out" if issue.get("timeout") else "unauthorized (401)" if "401" in text else "forbidden (403)" if "403" in text else issue.get("reason") or f"{issue['stage']} failed"
    if attempts > 1: reason += " after retry"
    reason += f" ({elapsed:.1f}s)"
    if issue.get("stderr"): reason += "; " + issue["stderr"]
    return notice("Codex", reason)
def codex_usage():
    home = Path(os.environ.get("CODEX_HOME", "~/.codex")).expanduser()
    auth = load(home / "auth.json"); tokens = auth.get("tokens") if isinstance(auth.get("tokens"), dict) else {}
    account, cached = tokens.get("account_id"), load(CODEX_CACHE)
    if cached.get("account") != account: cached = {"account": account}
    binary = os.environ.get("CODEX_BIN") or shutil.which("codex") or str(Path.home() / ".local/bin/codex")
    started, issues, windows, credits = time.monotonic(), [], None, None
    for attempt in range(2):
        result, issue = codex_attempt(binary, min(started + 14, time.monotonic() + 7))
        if not issue:
            windows = codex_windows(result.get("rateLimits"))
            weekly = quota(windows.get("10080"), 10080)
            if weekly.get("used") is not None and weekly.get("reset") and weekly["reset"] > time.time():
                credits = result.get("rateLimitResetCredits"); break
            issue = {"stage": "rate limits", "reason": "weekly window missing" if windows else "usage data missing", "message": "", "stderr": "", "timeout": False, "elapsed_ms": 0}
            windows = None
        issues.append(issue)
        if attempt or not (issue.get("timeout") or issue.get("reason")): break
    elapsed = time.monotonic() - started
    diagnostic = {"at": now().isoformat(), "ok": windows is not None, "attempts": len(issues) + (windows is not None), "elapsed_ms": round(elapsed * 1000)}
    if windows is not None:
        cached = {"account": account, "observed_at": time.time(), "source": "network", "windows": windows, "credits": credits, "last_attempt": diagnostic}
        try: save(CODEX_CACHE, cached, 0o600)
        except OSError as exc: return codex_provider(windows, credits, error_text=notice("Codex", f"cache write failed ({exc.errno})"))
        return codex_provider(windows, credits)
    issue = issues[-1] if issues else {"stage": "lookup", "message": "budget exhausted", "stderr": "", "timeout": True}
    diagnostic.update(stage=issue["stage"], timeout=issue.get("timeout", False), reason=issue.get("reason", ""), message=issue.get("message", ""), stderr=issue.get("stderr", ""))
    observed = number(cached.get("observed_at")) or 0
    local_at, local_windows = codex_local_usage(home, observed) if observed else (0, None)
    known_reset = quota((cached.get("windows") or {}).get("10080"), 10080).get("reset")
    local_reset = quota((local_windows or {}).get("10080"), 10080).get("reset")
    if local_reset and local_reset == known_reset: cached.update(observed_at=local_at, source="session", windows=local_windows)
    cached["last_attempt"] = diagnostic
    try: save(CODEX_CACHE, cached, 0o600)
    except OSError as exc: issue["stderr"] = (issue.get("stderr", "") + f"; cache write failed ({exc.errno})").strip("; ")
    error_text = codex_error(issue, len(issues), elapsed)
    return codex_provider(cached.get("windows"), cached.get("credits"), number(cached.get("observed_at")), error_text)
def codex():
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        usage, resets = pool.submit(codex_usage), pool.submit(reset_info)
        data, resets = usage.result(), resets.result()
    credit = data.pop("_banked", "")
    if credit: resets["banked"] = credit
    data["global_resets"] = resets
    return data
def claude_home(): return Path(os.environ.get("CLAUDE_CONFIG_DIR", os.environ.get("CLAUDE_HOME", "~/.claude"))).expanduser()
def claude_token(home, failed_token=None):
    path = home / ".credentials.json"
    if not path.exists(): raise RuntimeError("sign in required")
    with Path(str(path) + ".lock").open("a+") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        stored = load(path); credentials = stored.get("claudeAiOauth")
        if not isinstance(credentials, dict): raise RuntimeError("sign in required")
        token, expires = credentials.get("accessToken"), number(credentials.get("expiresAt"))
        if expires and expires < 10**12: expires *= 1000
        if token and token != failed_token and expires and expires - time.time() * 1000 > 300000: return token
        refresh_token, scopes = credentials.get("refreshToken"), credentials.get("scopes")
        if not refresh_token: raise RuntimeError("OAuth refresh token missing")
        if not isinstance(scopes, list) or not all(isinstance(scope, str) for scope in scopes): raise RuntimeError("OAuth scopes missing")
        body = json.dumps({"grant_type": "refresh_token", "refresh_token": refresh_token, "client_id": CLAUDE_CLIENT_ID, "scope": " ".join(scopes)}).encode()
        payload = http(CLAUDE_TOKEN_URL, {"Accept": "application/json", "Content-Type": "application/json"}, body, 30)
        token, expires_in = payload.get("access_token"), number(payload.get("expires_in"))
        if not token or not expires_in: raise RuntimeError("OAuth refresh returned incomplete credentials")
        current = load(path); current_credentials = current.get("claudeAiOauth")
        if not isinstance(current_credentials, dict): raise RuntimeError("credentials changed during refresh")
        if current_credentials.get("refreshToken") != refresh_token:
            if current_credentials.get("accessToken"): return current_credentials["accessToken"]
            raise RuntimeError("credentials changed during refresh")
        credentials = current_credentials
        credentials.update(accessToken=token, refreshToken=payload.get("refresh_token", refresh_token), expiresAt=int(time.time() * 1000 + expires_in * 1000))
        refresh_expires = number(payload.get("refresh_token_expires_in"))
        if refresh_expires: credentials["refreshTokenExpiresAt"] = int(time.time() * 1000 + refresh_expires * 1000)
        if isinstance(payload.get("scope"), str): credentials["scopes"] = payload["scope"].split()
        current["claudeAiOauth"] = credentials; save(path, current, 0o600)
        return token
def claude_scoped(payload):
    limits = payload.get("limits")
    if not isinstance(limits, list): raise RuntimeError("usage response missing limits")
    rows = []
    for item in limits:
        if not isinstance(item, dict) or item.get("kind") != "weekly_scoped": continue
        name, percent = dig(item, "scope", "model", "display_name"), number(item.get("percent"))
        if not name or percent is None: raise RuntimeError("scoped weekly limit is incomplete")
        rows.append({"title": name, "data": quota({"used_percent": percent, "resets_at": item.get("resets_at")}, 10080)})
    return rows
def claude():
    home = claude_home()
    try:
        token = claude_token(home)
        headers = {"Authorization": f"Bearer {token}", "Accept": "application/json", "Content-Type": "application/json", "anthropic-beta": "oauth-2025-04-20", "User-Agent": "KDE-AI-Usage-Widget/1"}
        try: payload = http(CLAUDE_API_URL + "/api/oauth/usage", headers, timeout=5)
        except error.HTTPError as exc:
            if exc.code != 401: raise
            headers["Authorization"] = f"Bearer {claude_token(home, token)}"
            payload = http(CLAUDE_API_URL + "/api/oauth/usage", headers, timeout=5)
    except RuntimeError as exc: return blank_provider({"error": notice("Claude", str(exc))})
    except NETWORK_ERRORS as exc: return blank_provider({"error": failure("Claude", exc)})
    limits = {key: payload.get(key) for key in ("five_hour", "seven_day")}
    data = {"available": any(isinstance(value, dict) for value in limits.values()), "current": quota(limits["five_hour"], 300), "weekly": quota(limits["seven_day"], 10080)}
    problems = [f"{' and '.join(missing)} window missing"] if (missing := [label for key, label in (("five_hour", "5h"), ("seven_day", "weekly")) if not isinstance(limits[key], dict)]) else []
    try: scoped = claude_scoped(payload)
    except RuntimeError as exc: scoped, problems = [], problems + [str(exc)]
    if scoped: data["rows"] = [{"title": "5h", "data": data["current"]}, {"title": "Week", "data": data["weekly"]}] + scoped
    if problems: data["error"] = notice("Claude", "; ".join(problems))
    account = load(Path.home() / ".claude.json").get("oauthAccount")
    if isinstance(account, dict) and account.get("accountUuid"):
        data["_account"] = account["accountUuid"]
        if account.get("emailAddress"): data["account"] = account["emailAddress"]
    return data
def conversion_ratio(items):
    ratios = []
    key = lambda item: (item.get("current_reset"), item.get("weekly_reset"))
    for _, run in itertools.groupby((item for item in items if isinstance(item, dict)), key):
        run = list(run)
        current = number(run[-1].get("current_used")) - number(run[0].get("current_used"))
        weekly = number(run[-1].get("weekly_used")) - number(run[0].get("weekly_used"))
        if current >= 4 and 0 < weekly <= current: ratios.append(weekly / current)
    return median(ratios[-20:]) if ratios else None
def update_history(data, history):
    for name in PROVIDERS:
        if name not in data: continue
        provider = data[name]
        account = provider.pop("_account", None)
        items = history.get(name) if isinstance(history.get(name), list) else []
        if account and history.get(f"{name}_account") != account:
            items = []; history[name] = items; history[f"{name}_account"] = account
        values = {
            "current_used": number(provider["current"].get("used")), "current_reset": number(provider["current"].get("reset")),
            "weekly_used": number(provider["weekly"].get("used")), "weekly_reset": number(provider["weekly"].get("reset")),
        }
        if all(value is not None for value in values.values()) and (not items or values != items[-1]):
            items = (items + [values])[-300:]
            history[name] = items
        ratio, current, weekly = conversion_ratio(items), provider["current"], provider["weekly"]
        current_used, current_reset, weekly_used, weekly_reset = map(number, (current.get("used"), current.get("reset"), weekly.get("used"), weekly.get("reset")))
        if None in (ratio, current_used, current_reset, weekly_used, weekly_reset) or current_reset <= now().timestamp() or weekly_reset <= now().timestamp():
            continue
        capacity = 100 - current_used
        if current_reset < weekly_reset: capacity += math.ceil((weekly_reset - current_reset) / 18000) * 100
        reachable = min(100.0, weekly_used + capacity * ratio)
        if reachable < FULL_USED: weekly.update(pace=f"Behind: max {round(reachable)}%", color=COLORS["under"])
    try:
        save(HISTORY_CACHE, history)
    except OSError: pass
def jsonl(path):
    try:
        with path.open(encoding="utf-8", errors="replace") as lines:
            for line in lines:
                try:
                    yield json.loads(line)
                except json.JSONDecodeError: pass
    except OSError: return
def token_windows(): return {key: collections.defaultdict(collections.Counter) for key, _ in TOKEN_WINDOWS}
def add_tokens(windows, stamp, model, values):
    age = time.time() - stamp
    for key, seconds in TOKEN_WINDOWS:
        if seconds is None or age <= seconds: windows[key][model].update(values)
def event_at(item, key="timestamp"):
    stamp = moment(item.get(key))
    return stamp.timestamp() if stamp else None
def codex_sources():
    home = Path(os.environ.get("CODEX_HOME", "~/.codex")).expanduser()
    models, db = {}, home / "state_5.sqlite"
    try:
        with sqlite3.connect(db.resolve().as_uri() + "?mode=ro", uri=True) as connection:
            models = {str(Path(path)): model or "unknown" for path, model in connection.execute("select rollout_path, model from threads") if path}
    except sqlite3.Error: pass
    root = home / "sessions"
    # The sqlite-derived model rides in the cache signature, so relabelling a thread re-parses its rollout.
    return [(path, models.get(str(path), "unknown")) for path in (root.rglob("*.jsonl") if root.exists() else [])]
def codex_events(path, model):
    previous = collections.Counter()
    for item in jsonl(path):
        payload = item.get("payload") or {}
        # VS Code sessions leave threads.model NULL, so session_meta provenance is the only model record.
        candidate = next((value for value in (payload.get("model"), dig(payload, "collaboration_mode", "settings", "model"), dig(payload, "base_instructions", "provenance", "model")) if isinstance(value, str)), None)
        if candidate: model = candidate
        if item.get("type") != "event_msg" or payload.get("type") != "token_count": continue
        total = ((payload.get("info") or {}).get("total_token_usage") or {})
        if not total: continue
        current = collections.Counter({key: number(total.get(key), True) for key in ("input_tokens", "cached_input_tokens", "output_tokens", "total_tokens")})
        delta, previous = current - previous, current
        stamp = event_at(item)
        if stamp is not None: yield [None, stamp, model, {"input": delta["input_tokens"], "cached": delta["cached_input_tokens"], "output": delta["output_tokens"], "tokens": delta["total_tokens"]}]
def claude_sources():
    root = Path(os.environ.get("CLAUDE_HOME", "~/.claude")).expanduser() / "projects"
    return [(path, None) for path in (root.rglob("*.jsonl") if root.exists() else [])]
def claude_events(path, _):
    for item in jsonl(path):
        message = item.get("message") or {}
        usage = message.get("usage") or {}
        if item.get("type") != "assistant" or not isinstance(usage, dict): continue
        creation = usage.get("cache_creation") or {}
        write5, write1h = number(creation.get("ephemeral_5m_input_tokens"), True), number(creation.get("ephemeral_1h_input_tokens"), True)
        values = {"input": number(usage.get("input_tokens"), True), "write5": write5, "write1h": write1h, "read": number(usage.get("cache_read_input_tokens"), True), "output": number(usage.get("output_tokens"), True)}
        values["write_unknown"] = max(0, number(usage.get("cache_creation_input_tokens"), True) - write5 - write1h)
        values["tokens"] = sum(values.values())
        stamp = event_at(item)
        # Resumed sessions and subagent copies replay the same message into several files; the id keeps one count.
        if stamp is not None: yield [message.get("id") or item.get("requestId") or item.get("uuid") or f"{path}:{item.get('timestamp')}", stamp, message.get("model") or "unknown", values]
def kimi_sources():
    root = kimi_home() / "sessions"
    return [(path, None) for path in (root.rglob("wire.jsonl") if root.exists() else [])]
def kimi_events(path, _):
    for item in jsonl(path):
        usage = item.get("usage") or {}
        if item.get("type") != "usage.record" or item.get("usageScope") != "turn" or not isinstance(usage, dict): continue
        values = {"input": number(usage.get("inputOther"), True) + number(usage.get("inputCacheCreation"), True), "cached": number(usage.get("inputCacheRead"), True), "output": number(usage.get("output"), True)}
        values["tokens"] = sum(values.values())
        stamp = event_at(item, "time")
        if stamp is not None: yield [None, stamp, item.get("model") or "unknown", values]
def grok_sources():
    root = grok_home() / "sessions"
    return [(path, None) for path in (root.rglob("updates.jsonl") if root.exists() else [])]
def grok_events(path, _):
    for item in jsonl(path):
        params = item.get("params") or {}; usage = (params.get("update") or {}).get("usage")
        if not isinstance(usage, dict): continue
        meta = params["_meta"]; event = meta["eventId"]
        stamp = moment(meta["agentTimestampMs"])
        if stamp is None: continue
        for model, raw in usage["modelUsage"].items():
            values = {"input": int(raw["inputTokens"]), "cached": int(raw["cachedReadTokens"]), "output": int(raw["outputTokens"]), "tokens": int(raw["totalTokens"])}
            if "costUsdTicks" in raw: values["cost"] = int(raw["costUsdTicks"]) / 10**10
            else: values["uncosted"] = values["tokens"]
            yield [f"{event}:{model}", stamp.timestamp(), "grok-4.5" if model == "grok-4.5-build" else model, values]
def protobuf_varint(data, offset=0):
    value = shift = 0
    while offset < len(data):
        byte = data[offset]; offset += 1; value |= (byte & 127) << shift
        if byte < 128: return value, offset
        shift += 7
    raise ValueError("truncated protobuf varint")
def protobuf_fields(data):
    fields, offset = [], 0
    while offset < len(data):
        tag, offset = protobuf_varint(data, offset); field, wire = tag >> 3, tag & 7
        if wire == 0: value, offset = protobuf_varint(data, offset)
        elif wire == 1: value, offset = data[offset:offset + 8], offset + 8
        elif wire == 2:
            size, offset = protobuf_varint(data, offset); value, offset = data[offset:offset + size], offset + size
        elif wire == 5: value, offset = data[offset:offset + 4], offset + 4
        else: raise ValueError(f"unsupported protobuf wire type {wire}")
        fields.append((field, wire, value))
    return fields
def protobuf_first(fields, field, wire=None):
    return next((value for key, kind, value in fields if key == field and (wire is None or kind == wire)), None)
def agy_sources():
    def companion(path):
        # sqlite writes land in -wal before the .db mtime moves, so the wal stamp guards the signature.
        try: stat = Path(str(path) + "-wal").stat()
        except OSError: return None
        return [stat.st_mtime_ns, stat.st_size]
    return [(path, companion(path)) for path in (agy_home() / "conversations").glob("*.db")]
def agy_events(path, _):
    with sqlite3.connect(f"file:{path}?mode=ro", uri=True) as connection:
        timestamps = {}
        for index, metadata in connection.execute("select idx, metadata from steps"):
            stamp = protobuf_first(protobuf_fields(metadata), 1, 2)
            if stamp: timestamps[index] = protobuf_first(protobuf_fields(stamp), 1, 0)
        for index, data in connection.execute("select idx, data from gen_metadata"):
            top = protobuf_fields(data); generation = protobuf_first(top, 1, 2)
            if not generation: continue
            generation = protobuf_fields(generation); stats = protobuf_first(generation, 4, 2)
            if not stats: continue
            stats = protobuf_fields(stats)
            event = protobuf_first(stats, 7, 2) or protobuf_first(stats, 11, 2) or f"{path}:{index}".encode()
            model = (protobuf_first(generation, 19, 2) or b"unknown").decode(errors="replace")
            input_tokens = protobuf_first(stats, 2, 0) or 0
            output_tokens = protobuf_first(stats, 3, 0) or 0
            prompt_tokens = protobuf_first(stats, 5, 0) or 0
            if model.startswith("gemini-3.1-pro") and prompt_tokens > 200000: model = "gemini-3.1-pro (>200k)"
            total_input = max(input_tokens, prompt_tokens)
            values = {"input": total_input, "cached": max(0, prompt_tokens - input_tokens), "output": output_tokens, "tokens": total_input + output_tokens}
            packed, offset, references = protobuf_first(top, 2, 2) or b"", 0, []
            while offset < len(packed):
                reference, offset = protobuf_varint(packed, offset); references.append(reference)
            stamp = moment(max((timestamps.get(reference) or 0 for reference in references), default=0))
            if stamp is not None: yield [event.hex(), stamp.timestamp(), model, values]
def model_cost(provider, model, values):
    if provider == "grok": return number(values.get("cost")) if "cost" in values else None
    if provider == "agy":
        rates = AGY_PRICES.get(model)
        if not rates and (claude_rates := CLAUDE_PRICES.get(model.removesuffix("-thinking"))): rates = (claude_rates[0], claude_rates[3], claude_rates[4])
    else: rates = {"codex": OPENAI_PRICES, "claude": CLAUDE_PRICES, "kimi": KIMI_PRICES}[provider].get(model)
    if not rates: return None
    if provider == "claude":
        usage = (values["input"], values["write5"] + values["write_unknown"], values["write1h"], values["read"], values["output"])
    elif provider in ("codex", "agy"):
        usage = (max(0, values["input"] - values["cached"]), values["cached"], values["output"])
    else:
        usage = (values["input"], values["cached"], values["output"])
    return sum(tokens * rate for tokens, rate in zip(usage, rates)) / 1_000_000
def compact(value):
    if value >= 1_000_000_000: return f"{value / 1_000_000_000:.2f}B"
    if value >= 1_000_000: return f"{value / 1_000_000:.1f}M"
    if value >= 1_000: return f"{value / 1_000:.1f}K"
    return str(value)
def money(value): return f"${value:,.0f}"
def error_signature(text): return re.sub(r" \([\d.]+s\)(?:;.*)?$", "", text.split(" - ", 1)[-1])
def error_history(data, selected):
    stored = load(ERROR_CACHE); items, active = stored.get("items", []), stored.get("active", {})
    cutoff = time.time() - 86400
    items = [item for item in items if isinstance(item, list) and len(item) == 3 and number(item[2], True) >= cutoff] if isinstance(items, list) else []
    active = active if isinstance(active, dict) else {}
    current = {name: data[name].get("error", "") for name in selected if name in data}
    for name, text in current.items():
        if text and active.get(name) != error_signature(text): items.append([name, text, int(time.time())])
    try: save(ERROR_CACHE, {"items": items[-20:], "active": {name: error_signature(text) for name, text in current.items() if text}})
    except OSError: pass
    return list(reversed([text for name, text, at in items if name in selected][-3:]))
def events_cache(provider): return CACHE / f"token-events-{provider}.jsonl.gz"
def cached_lines(path):
    try:
        with gzip.open(path, "rt", encoding="utf-8") as handle:
            yield from handle
    except FileNotFoundError: return
    except (OSError, EOFError) as issue:
        print(f"token cache {path.name} unreadable ({issue}); re-parsing the rest from source", file=sys.stderr)
LIFETIME, HORIZON = TOKEN_WINDOWS[0][0], max(seconds for _, seconds in TOKEN_WINDOWS if seconds)
def settle(events):
    """Split events the widest bounded window can never reach again; those only ever land in lifetime.

    Only un-deduped events fold, since a rollup has no ids left to match against other files.
    """
    cutoff, live, settled = time.time() - HORIZON, [], collections.defaultdict(collections.Counter)
    for event in events:
        event_id, stamp, model, values = event
        if event_id is None and stamp < cutoff: settled[model].update(values)
        else: live.append(event)
    return live, {model: dict(values) for model, values in settled.items()}
def signature(path, extra):
    try: stat = path.stat()
    except OSError: return None
    return [stat.st_mtime_ns, stat.st_size, extra]
def scan_provider(provider, sources, extract):
    """Fold one provider's history into windows, parsing only files whose signature moved.

    Session logs are append-only, so an unchanged (mtime, size, extra) means the stored parse still holds.
    The cache is streamed one file at a time, keeping memory flat as history grows.
    """
    pending = {str(path): (path, extra) for path, extra in sources}
    windows, best, stats = token_windows(), {}, {"provider": provider, "reused": 0, "parsed": 0, "mb": 0.0}
    def absorb(entry):
        for model, values in (entry.get("settled") or {}).items(): windows[LIFETIME][model].update(values)
        for event_id, stamp, model, values in entry["events"]:
            if event_id is None:
                add_tokens(windows, stamp, model, values); continue
            # Streaming records a message id twice: mid-flight with a partial output count, then complete.
            # Keeping the largest total picks the finished copy and makes the result independent of read order.
            winner = best.get(event_id)
            if winner is None or values["tokens"] > winner[2]["tokens"]: best[event_id] = (stamp, model, values)
    cache = events_cache(provider)
    tmp = cache.with_suffix(".tmp")
    CACHE.mkdir(parents=True, exist_ok=True)
    with gzip.open(tmp, "wt", encoding="utf-8") as out:
        for line in cached_lines(cache):
            try: entry = json.loads(line)
            except json.JSONDecodeError:
                print(f"token cache {cache.name} holds an unreadable entry; re-parsing that file", file=sys.stderr); continue
            source = pending.get(entry["path"])
            if source is None or entry["sig"] != signature(*source): continue
            del pending[entry["path"]]
            absorb(entry); out.write(line); stats["reused"] += 1
        for path, extra in pending.values():
            sig = signature(path, extra)
            if sig is None: continue
            events, settled = settle(extract(path, extra))
            entry = {"path": str(path), "sig": sig, "events": events, "settled": settled}
            absorb(entry); out.write(json.dumps(entry, separators=(",", ":")) + "\n")
            stats["parsed"] += 1; stats["mb"] += sig[1] / 1048576
    tmp.replace(cache)
    for stamp, model, values in best.values(): add_tokens(windows, stamp, model, values)
    stats["mb"], stats["deduped"] = round(stats["mb"], 1), len(best)
    return windows, stats
def scan_token_usage(names=PROVIDERS):
    scanners = {"codex": (codex_sources, codex_events), "claude": (claude_sources, claude_events), "kimi": (kimi_sources, kimi_events), "grok": (grok_sources, grok_events), "agy": (agy_sources, agy_events)}
    result, report = {}, []
    for provider in names:
        started = time.monotonic()
        sources, extract = scanners[provider]
        windows, stats = scan_provider(provider, sources(), extract)
        stats["seconds"] = round(time.monotonic() - started, 2)
        result[provider] = {key: {model: dict(values) for model, values in windows[key].items()} for key, _ in TOKEN_WINDOWS}
        report.append(stats); print("token scan " + json.dumps(stats, separators=(",", ":")), file=sys.stderr)
    result["scan"] = report
    return result
def summarize_tokens(providers):
    rows, unpriced = [], set()
    for key, _ in TOKEN_WINDOWS:
        row = {"key": key, "providers": {}}
        for provider, windows in providers.items():
            total, cost, uncosted, models = 0, 0.0, 0, []
            for model, values in windows[key].items():
                total += values["tokens"]
                uncosted += values.get("uncosted", 0)
                estimate = model_cost(provider, model, values)
                if estimate is None and values["tokens"] > values.get("uncosted", 0): unpriced.add(model)
                elif estimate is not None:
                    cost += estimate
                    if values["tokens"] > 0: models.append((model, estimate, bool(values.get("uncosted"))))
            models.sort(key=lambda item: (-item[1], item[0]))
            suffix = "+" if uncosted else ""
            row["providers"][provider] = {"tokens": compact(total), "cost": money(cost) + suffix, "models": [{"name": model, "cost": money(estimate) + ("+" if partial else "")} for model, estimate, partial in models], "note": f"{compact(uncosted)} tokens lack cost data" if uncosted else ""}
        rows.append(row)
    note = "Unpriced models excluded from cost: " + ", ".join(sorted(unpriced)) if unpriced else ""
    return {"windows": rows, "note": note}
def refresh_tokens():
    CACHE.mkdir(parents=True, exist_ok=True)
    with (CACHE / "token-stats.lock").open("a+") as lock:
        try: fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        except BlockingIOError: return 0
        data = load(TOKEN_CACHE); data.update(scan_token_usage(selected_providers())); save(TOKEN_CACHE, data)
    return 0
def token_stats(selected):
    data = load(TOKEN_CACHE)
    missing = [provider for provider in selected if not isinstance(data.get(provider), dict)]
    if not missing:
        if time.time() - TOKEN_CACHE.stat().st_mtime > 600:
            subprocess.Popen([sys.executable, __file__, "--refresh-tokens", "--providers=" + ",".join(selected)], stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, start_new_session=True, creationflags=subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0)
    else:
        data.update(scan_token_usage(missing)); save(TOKEN_CACHE, data)
    return summarize_tokens({name: data[name] for name in selected})
def selected_providers():
    value = next((arg.partition("=")[2] for arg in sys.argv if arg.startswith("--providers=")), "")
    selected = tuple(name for name in value.split(",") if name in PROVIDERS)
    return selected or PROVIDERS
def snapshot():
    history = load(HISTORY_CACHE)
    jobs = {"claude": claude, "codex": codex, "kimi": kimi, "grok": grok, "agy": agy}
    selected = selected_providers()
    with concurrent.futures.ThreadPoolExecutor(max_workers=len(selected)) as pool:
        futures = {name: pool.submit(jobs[name]) for name in selected}
        data = {name: future.result() for name, future in futures.items()}
    data["tokens"] = token_stats(selected)
    data["errors"] = error_history(data, selected)
    update_history(data, history)
    print(json.dumps(data, separators=(",", ":")))
    return 0
def capture_claude():
    try:
        data = json.loads(sys.stdin.read())
    except json.JSONDecodeError: return 0
    model = (data.get("model") or {}).get("display_name") or (data.get("model") or {}).get("id") or "Claude"
    limits = data.get("rate_limits") or {}
    print(f"{model} | 5h used {(limits.get('five_hour') or {}).get('used_percentage', '--')}% | 7d used {(limits.get('seven_day') or {}).get('used_percentage', '--')}%")
    return 0
if __name__ == "__main__":
    raise SystemExit(refresh_tokens() if "--refresh-tokens" in sys.argv else capture_claude() if "--capture-claude-statusline" in sys.argv else snapshot())
