import subprocess

# Windows shutdown 的延时时间单位为秒：30 分钟 = 1800 秒。
result = subprocess.run(
    ["shutdown", "/s", "/t", "1800"],
    capture_output=True,
    text=True,
    encoding="utf-8",
    errors="replace",
)
print(f"returncode={result.returncode}")
if result.stdout:
    print(result.stdout.strip())
if result.stderr:
    print(result.stderr.strip())
