"""Cross-platform quota and pipe-protocol regression checks; no network access."""
import datetime as dt
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import unittest

source = Path(__file__).resolve().parent / 'widget_snapshot.py'
spec = importlib.util.spec_from_file_location('widget_snapshot', source)
widget = importlib.util.module_from_spec(spec)
spec.loader.exec_module(widget)

class CollectorTests(unittest.TestCase):
    def test_quota_and_portable_dates(self):
        reset = (dt.datetime.now(dt.timezone.utc) + dt.timedelta(days=12)).isoformat()
        result = widget.quota({'utilization': 45, 'resets_at': reset}, 10080)
        self.assertEqual(result['used'], 45)
        self.assertIn('Resets', result['reset_label'])
        self.assertIn('test', widget.notice('Claude', 'test'))
        self.assertTrue(widget.short_time(reset))

    def test_pipe_rpc_buffers_multiple_responses(self):
        code = "import sys,json; sys.stdin.readline(); print(json.dumps({'method':'notification'})); print(json.dumps({'id':1,'result':{'ok':1}})); print(json.dumps({'id':2,'result':{'ok':2}}),flush=True); sys.stdin.readline()"
        process = subprocess.Popen([sys.executable, '-u', '-c', code], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        try:
            self.assertEqual(widget.rpc(process, {'id':1}, 2), {'ok':1})
            self.assertEqual(widget.rpc(process, {'id':2}, 2), {'ok':2})
        finally:
            process.wait(timeout=3); process.stdin.close(); process.stdout.close()

    def test_pipe_rpc_timeout(self):
        process = subprocess.Popen([sys.executable, '-u', '-c', 'import time; time.sleep(10)'], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True)
        try:
            started = time.monotonic()
            with self.assertRaises(TimeoutError): widget.rpc(process, {'id':1}, .1)
            self.assertLess(time.monotonic()-started, 2)
        finally:
            process.kill(); process.wait(); process.stdin.close(); process.stdout.close()

    def test_lock_excludes_other_process(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / 'test.lock'
            code = "import importlib.util,sys; s=importlib.util.spec_from_file_location('w',sys.argv[1]); w=importlib.util.module_from_spec(s); s.loader.exec_module(w); f=open(sys.argv[2],'a+');\ntry: w.fcntl.flock(f,w.fcntl.LOCK_EX|w.fcntl.LOCK_NB)\nexcept BlockingIOError: sys.exit(23)"
            with path.open('a+') as lock:
                widget.fcntl.flock(lock, widget.fcntl.LOCK_EX)
                result = subprocess.run([sys.executable, '-c', code, str(source), str(path)])
                self.assertEqual(result.returncode, 23)
            result = subprocess.run([sys.executable, '-c', code, str(source), str(path)])
            self.assertEqual(result.returncode, 0)

if __name__ == '__main__': unittest.main()
