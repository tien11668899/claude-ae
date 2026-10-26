# claude-ae

讓 Claude（Claude Code）直接操作 Adobe After Effects 做動態圖像：建合成、加圖層、套用調好的動畫、加效果，**並渲染畫面回來自己看、自己修**。

```
Claude Code ──MCP(stdio)──▶ server\dist\claude-ae.exe ──.spool 資料夾──▶ AE 裡的 Claude Bridge 面板 ──▶ ExtendScript
                                   │
                                   └─ WPF：縮圖、拼接預覽圖、運動曲線圖 → 以圖片回傳給 Claude
```

- **不需要 Node / Python / npm**：伺服器是 .NET 8（這台電腦已裝 SDK），圖片處理用 Windows 內建的 WPF。
- **不需要 CEP 擴充或改登錄檔**：AE 端是一般的 ScriptUI 面板，用 `app.scheduleTask` 每 80ms 檢查一次指令資料夾。
- 每個會改動專案的指令都是 **AE 裡的一個復原步驟**（Ctrl+Z 一次就撤回 Claude 的一個動作），而且**全有或全無**：指令中途失敗會自動復原，不會留下做一半的東西。
- 每個工作階段第一次改動前，會把**上次存檔的 .aep 複製到 `claude-backups\`**。
- 已在 **After Effects 2023（23.0）** 實機測試過所有工具（2026-10-26）。

## 安裝（AE 裝好之後）

1. 在 PowerShell 執行（寫入 AE 的 Program Files 資料夾時會跳出系統管理員確認）：
   ```powershell
   .\install.ps1
   ```
2. 開 After Effects：
   - **編輯 > 偏好設定 > 指令碼與運算式** → 勾選「**允許指令碼寫入檔案和存取網路**」
   - **視窗 > Claude Bridge.jsx** → 面板顯示 `Running` 就代表連上了（可以把它停靠在任何地方）
   - 剛裝好面板時要**重開 AE** 才會出現在選單裡。每次開 AE 後都要打開這個面板；從外部用 `AfterFX.exe -r` 送腳本進正在執行的 AE，在這台電腦上無效。
3. 把伺服器註冊到 Claude Code（使用者層級，所有工作階段都能用；這台電腦已在 2026-10-25 註冊過）：
   ```powershell
   claude mcp add --scope user claude-ae -- "C:\Users\user\source\claude-ae\server\dist\claude-ae.exe"
   ```
   重新建置伺服器的方法見下方「測試」一節的說明。

## 工具一覽

| 類別 | 工具 |
|---|---|
| 狀態與檢視 | `ae_status`、`ae_project`、`ae_comp_info`、`ae_layer_props`、`ae_list`（效果、字型、輸出範本、緩動、預設） |
| 建構 | `ae_create_comp`、`ae_add_layer`（文字、形狀、純色、調整、空物件、攝影機、燈光、素材）、`ae_import`、`ae_edit_layer`（父子、軌道遮罩、預先合成、刪除等） |
| 動畫 | `ae_animate`（調好的預設）、`ae_set_keyframes`（真正的貝茲緩動）、`ae_expression`（彈簧、wiggle、loop）、`ae_set_props`、`ae_effect` |
| **回看** | `ae_render_frame`（單張）、`ae_contact_sheet`（多格拼接）、`ae_motion_curve`（數值與速度曲線＋自動判讀） |
| 輸出 | `ae_render_video`（自動選最高位元率的 H.264）、`ae_save`、`ae_open_comp` |
| 復原與維護 | `ae_undo`（撤回 Claude 自己上一步）、`ae_reload_bridge`（改了 `ClaudeBridge.jsx` 後就地重新載入，不用重開面板） |
| 萬用 | `ae_eval`（直接執行 ExtendScript） |

### 動畫庫重點

- **緩動是精確換算的**：`out_expo`、`in_out_expo`、`out_back` 等，就是網頁上的 cubic-bezier 曲線，換算成 AE 關鍵影格的速度與影響值，而不是 AE 預設那種軟軟的 Easy Ease。也可以直接給 `[x1,y1,x2,y2]`。
- **預設動畫**：`fade`、`slide_up/down/left/right`、`scale`、`pop`（回彈）、`blur`、`wipe`。文字專用：`type_on`（打字機）、`cascade`（逐字浮現）、`tracking`（字距收合）。可以組合，例如 `["slide_up","blur"]`。多個圖層可以用 `stagger` 錯開進場時間。
- **彈簧運算式**：關鍵影格只決定目標值與起始時間，動作交給物理彈簧，回彈很自然。適合進場；退場縮到 0 請用關鍵影格加 `in_back`，否則彈簧會在 0 附近反彈。
- **運動曲線判讀**：`ae_motion_curve` 會把進場、退場等不同段落分開判斷（例如「快速出發後緩停」、「加速離場」、「沒有緩動、突然停止」、「接近等速、看起來很機械」、「超出終點 9%」）。

### 風格規範

`style\default.json`：色票、字型、字級、安全邊界、動態時間。複製一份改名，就是另一套風格。Claude 會先讀規範再動手。

- 預設字型：英文用 **Bahnschrift**（Bold / SemiBold / Light / BoldCondensed），中文用**微軟正黑體**。
- **Segoe UI 在這台電腦的 AE 裡看不到**，會被換成 Times New Roman。AE 23 沒有字型清單 API，所以只要 AE 換掉了指定字型，工具就會回傳警告。

## 測試（不需要 AE）

```powershell
cscript //nologo //E:jscript tests\jsx-test.js ae\ClaudeBridge.jsx   # AE 端：ES3 語法、JSON、緩動換算
.\server\dist\claude-ae.exe --selftest                               # 伺服器：圖片、假 AE 往返、工具表
```

不經過 MCP、直接對正在執行的 AE 呼叫單一工具（除錯用，圖片存到 `.spool\cli\`）：

```powershell
'{"comp":"Claude Test","count":9}' | .\server\dist\claude-ae.exe --call ae_contact_sheet
```

重新建置伺服器時，如果有其他 Claude 工作階段正在使用 `server\dist\claude-ae.exe`，檔案會被鎖住。可以先把 `claude-ae.exe` / `claude-ae.dll` 改名（執行中的檔案可以改名，不能覆蓋），再建置。

## 疑難排解

- **`ae_status` 顯示 OFFLINE**：AE 沒開、面板沒開，或 AE 正停在某個對話框上（對話框會擋住腳本）。
- **面板跳出「cannot write files」**：見安裝步驟 2 的偏好設定。
- **某個屬性路徑找不到**：用 `ae_layer_props` 查，每個節點都會附上可以直接使用的 `path`。
- 指令檔放在 `.spool\`，伺服器啟動時會自動清掉超過 10 分鐘的舊檔。
