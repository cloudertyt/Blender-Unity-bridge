$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir
python -m uvicorn mml_bridge_server:app --host 127.0.0.1 --port 8000
