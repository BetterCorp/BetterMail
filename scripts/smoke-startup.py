"""Linux CI smoke check: launch a published app with an isolated temporary profile."""
import os
from pathlib import Path
import secrets
import signal
import subprocess
import sys
import tempfile
import time

with tempfile.TemporaryDirectory(prefix="bettermail-startup-") as temporary:
    env = os.environ.copy()
    env.update(XDG_DATA_HOME=temporary, XDG_CONFIG_HOME=temporary,
               BETTERMAIL_DATABASE_KEY=secrets.token_hex(32))
    with tempfile.TemporaryFile(mode="w+") as log:
        process = subprocess.Popen(["dotnet", str(Path(sys.argv[1]).resolve())], env=env,
                                   stdout=log, stderr=log, start_new_session=True)
        try:
            deadline = time.monotonic() + 12
            while time.monotonic() < deadline:
                if process.poll() is not None:
                    log.seek(0)
                    raise RuntimeError(f"Application exited early ({process.returncode}):\n{log.read()}")
                time.sleep(0.2)
            database = Path(temporary) / "BetterMail" / "mail.db"
            if not database.exists() or database.stat().st_size == 0:
                raise RuntimeError("Application did not initialize its local database")
            print("Published app remained running and initialized an isolated database.")
        finally:
            if process.poll() is None:
                os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=5)
                except subprocess.TimeoutExpired:
                    os.killpg(process.pid, signal.SIGKILL)
                    process.wait()
