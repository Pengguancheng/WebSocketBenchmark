# WebSocketBenchmark

`RelayTools.slnx` 包含三個可啟動專案與共用 `RelayTools.Core`，全部使用 .NET 10：

| 專案 | 職責 | 預設端點 |
|---|---|---|
| RelayTools.KestrelServer | Kestrel WebSocket adapter | ws://127.0.0.1:5081/chat |
| RelayTools.HttpListenerServer | HttpListener WebSocket adapter | ws://127.0.0.1:5082/chat |
| RelayTools.Client | 同時模擬兩端的使用者，獨立送收 | 連接上述兩端 |
| RelayTools.Core | 同房廣播、完整訊息讀取、序列發送、NLog 與設定 | 類別庫 |

兩個 Server 為獨立 OS 程序，使用相同聊天室核心與日誌設定；廣播包含發送者。每條連線的發送以 SemaphoreSlim 序列化。核心支援 text/binary、分段完整讀取及 close，單則訊息上限 1 MiB。

兩個 Server 各有實體入口：[Kestrel Program.cs](src/RelayTools.KestrelServer/Program.cs) 負責 WebApplication 啟動與 WebSocket 接受；[HttpListener Program.cs](src/RelayTools.HttpListenerServer/Program.cs) 負責 listener 啟動、接受與連線工作追蹤。兩者固定使用自己的 Server 模式，再交由同一 `RelayTools.Core.ChatRoom` 處理聊天室；沒有共用入口的模式切換分支。

## 設定

請修改 **repo 根目錄的 `relaytools.json`**，不是 bin 內副本。三個專案及腳本均在啟動時讀取這份 JSON；不支援熱更新。可傳 `--config 絕對路徑`（腳本為 `-Config`）選其他設定。個別啟動時從工作目錄及執行檔目錄向上尋找這份檔案；console 與 NLog `started` 均列出實際絕對路徑及有效參數。

```json
{
  "ClientsPerServer": 100,
  "MessagesPerSecondPerClient": 1,
  "PayloadBytes": 256,
  "WarmupSeconds": 2,
  "DurationSeconds": 0,
  "KestrelPort": 5081,
  "HttpListenerPort": 5082,
  "Room": "lobby",
  "Endpoints": "both",
  "Results": "results/manual"
}
```

`DurationSeconds=0` 持續執行至 Ctrl+C。100 人代表每個 Server 各 100 條連線，Client 共 200 條；每則向同房所有人廣播，因此每端每秒約 100 次輸入、10,000 次輸出。`Endpoints` 可設 `both`、`kestrel` 或 `httplistener`，port 可調，Server 僅監聽 127.0.0.1。訊息大小含 24 bytes header，允許 24 至 1,048,576 bytes。

## 一鍵執行

在 PowerShell 7 執行：

Windows PowerShell 5.1 不支援此腳本使用的新版 .NET API。腳本已宣告 `#Requires -Version 7.0`；請開啟 PowerShell 7，或在目前視窗使用 `pwsh -NoProfile -File ./scripts/run.ps1`。

```powershell
./scripts/run.ps1
```

腳本先 Release build，讀取 JSON，建立專屬 results/runId，啟動對應 Server 與 Client，列出 PID、設定及日誌位置。若 dotnet 不在 PATH，會尋找使用者 `.dotnet/dotnet.exe` 或 Program Files，亦可傳 `-Dotnet '完整路徑'`。腳本不會把未指定的 CLI 預設值蓋到 JSON。

短時間 smoke（明確覆寫，不執行 100 人持續模式）：

```powershell
./scripts/run.ps1 -Clients 3 -Rate 1 -PayloadBytes 256 -Warmup 2 -Duration 5
```

其他覆寫：`-Room`、`-KestrelPort`、`-HttpListenerPort`、`-Endpoints`、`-Results`；已自行編譯時可加 `-SkipBuild`。有限時間執行完成或 Ctrl+C 時，腳本以 stop marker 通知本次子程序，等待正常結束；逾時只強制停止自己持有的 child process。日誌及 `run-summary.json` 保留，包含 exit code 與 forced PID。沒有依名稱結束其他程序。HttpListener 若出現 access denied，請保留實際錯誤；工具不會新增全域 URLACL 或變更權限。

## Rider 個別執行

開啟 `RelayTools.slnx`，選 Release，以不附加 debugger 的方式先啟動兩個 Server，再啟動 Client。三者均讀 repo JSON。預設個別執行日誌在 `results/manual`；每次實驗請使用空的新 Results 路徑，並將相同 `--results` 與 `--run-id` 傳給三者，以免混用前次停止／階段 marker。個別執行 Ctrl+C 只停止該程序；先停止 Client 完成 close，再停止兩個 Server。一鍵腳本會自動管理共同 runId 及唯一結果目錄。

個別程序可覆寫：`--clients`、`--rate`、`--payload-bytes`、`--warmup`、`--duration`、`--kestrel-port`、`--httplistener-port`、`--room`、`--endpoints`、`--run-id`、`--results`。覆寫優先於 JSON，只有啟動時生效。

## 日誌與比較

NLog 每秒輸出一行 JSON 到 `{mode}-{PID}.jsonl`，無逐包日誌。`started` 記 runtime、OS、CPU 數及有效設定；`metrics` 包含 runId、PID、elapsed、phase、CPU total/delta ms、working set/private/managed heap bytes、GC 0/1/2 次數、各端點 active/sent/received/errors/closed。收發與 GC 是本次程序累計值，不在 phase 切換時歸零；階段為 setup/warmup/measure/drain/closing，跨階段的首個資源採樣區間應排除比較。

`cpuOneCorePercent = CPU delta ms / wall interval ms * 100`，可超過 100%；`cpuMachinePercent` 再除以 `cpuMachineDenominator`（Environment.ProcessorCount）。CPU total 是程序啟動以來累積量。

Client 的兩端每位使用者使用相同排程原點、序號及 payload；獨立 SendAsync 與持續接收，沒有兩端共用串行發送。header 內含同機 Stopwatch 排程時間，因此 `ScheduledLatency` 是「預定發送至接收」時間，包含 client 排程等待、傳送、Server 與回傳；不是純網路延遲或 RTT，不適用跨機。warmup/measure latency 與接收數依訊息 header 分開計算，不混入彼此。僅輸出 mean/max，不提供百分位。最後 `broadcast_check` 核對每端 received = sent × clients，正常 close 後應看到 active=0、closed=clients、errors=0。`connection_closed` 與 `stopped` 可追溯停止。

比較時固定 runtime、Release、不附 debugger、同參數與相同日誌頻率。雙端同機同時執行會競爭 CPU／記憶體／網路，Client 自身也會消耗資源；可用 `-Endpoints kestrel` 與 `-Endpoints httplistener` 交替單端跑作確認。這是聊天室工具，不含 RS 業務、快取或 gRPC；短 smoke 只驗證運作及日誌，不代表效能排名或普遍勝負。
