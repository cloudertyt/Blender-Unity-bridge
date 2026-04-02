# Unity `My project MML` 通信连接说明

## 文件整理结果
- 后端服务：`D:\Code-2\MML_Bridge\mml_bridge_server.py`
- 启动脚本：`D:\Code-2\MML_Bridge\start_mml_bridge.ps1`
- 依赖文件：`D:\Code-2\MML_Bridge\requirements-mml-bridge.txt`
- 数据库文件：`D:\Code-2\MML_Bridge\mml_bridge.db`
- Unity 脚本：`D:\UnityHub\My project MML\Assets\Scripts\MMLBridge\MmlBridgeClient.cs`

## 一次性安装依赖
```powershell
cd D:\Code-2\MML_Bridge
python -m pip install -r requirements-mml-bridge.txt
```

## 启动通信服务
```powershell
cd D:\Code-2\MML_Bridge
.\start_mml_bridge.ps1
```

## 在 Unity 侧验证
- 打开项目：`D:\UnityHub\My project MML`
- 点击 Play
- 在 Unity Console 看到以下日志即连接成功：
  - `[MML Bridge] Connected: ...`
  - `[MML Bridge] Event sent: ...`

## 后端接口
- `GET /health`
- `POST /collect`
- `WS /ws/chat`
