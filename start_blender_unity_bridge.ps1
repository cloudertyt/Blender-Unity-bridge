$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir
python -m uvicorn blender_unity_bridge_server:app --host 127.0.0.1 --port 8000
