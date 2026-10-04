# SharpInterpreter

[English](#english) | [繁體中文](#繁體中文)

<a name="english"></a>

## English

A C# code interpreter that runs in the browser. Roslyn and the .NET runtime are compiled to WebAssembly and
run in a Web Worker of a hidden page, so scripts an AI model writes (or a user types) are compiled and run
on the user's own device, inside the browser's sandbox: no server to run code on, no network, and no disk
beyond the files the host page passes in.

It is meant for "code interpreter" features in chat apps: instead of doing arithmetic in its head, the
model writes a short C# script that reads the user's CSV, computes exact numbers with LINQ, prints them,
and draws charts that the host page renders.

Extracted from [Harness.WinUI](https://github.com/breezy89757/Harness.WinUI).

### How it works

```
host page ── postMessage { id, code, files } ──▶ runner page (hidden iframe, its own origin)
                                                   runner.js ──▶ Web Worker (worker.js)
                                                                 .NET in WebAssembly: Roslyn compiles
                                                                 in memory → Assembly.Load → run
host page ◀── { type: 'result', id, result } ──── { Ok, Output, Error, Charts, CompileMs, RunMs }
```

- **Runner** (`src/SharpInterpreter.Runner`): a static site with no UI. `runner.js` relays scripts to a Web
  Worker that runs .NET, and ends the worker when a script runs too long (30 seconds by default). It works
  in an iframe (talks to `window.parent`) or in a WebView2 (`chrome.webview`).
- **Engine** (`src/SharpInterpreter.Engine`): compiles a script with Roslyn (top-level statements), loads it,
  runs it and captures `Console` output. Also runs on regular .NET, which is how the tests run.
- **Script API** (`src/SharpInterpreter.Script`): what scripts can call (below).
- **Host client** (`host/sharp-interpreter.js`): creates the sandboxed iframe and runs one script at a time.

The runner is about 11 MB brotli-compressed. Loading it takes a few seconds the first time; the first
compile takes 1 to 2 seconds while Roslyn warms up, and after that a compile plus run typically takes 30 to
150 ms. Because scripts run in a worker, the host page stays responsive while they run.

### Script API

Imported by default: `System`, `System.Linq`, `System.Collections.Generic`, `System.Globalization`,
`SharpInterpreter.Script`.

| API | |
|---|---|
| `Table.Read("sales.csv")` | A CSV file as a table: `.Columns`, `.Rows`. First line is the header; Excel-style quotes |
| `row["column"]` | The text. An unknown column throws an error that lists the real ones |
| `row.Num("column")` | A double; ignores thousands separators, `%`, `$`, `NT`; empty is 0 |
| `row.Date("column")` | A `DateTime` (invariant culture) |
| `Files.ReadText("notes.txt")` | A file's text (UTF-8) |
| `Out.Table(rows)` | Prints objects (e.g. anonymous types) as a Markdown table |
| `Chart.Bar / Line / Pie` | `(title, labels, values)`, or several series: `(title, labels, new Series(name, values), …)` |

```csharp
var sales = Table.Read("sales.csv");
var byRegion = sales.Rows
    .GroupBy(r => r["region"])
    .Select(g => new { Region = g.Key, Revenue = g.Sum(r => r.Num("revenue")) })
    .ToList();

Out.Table(byRegion);
Chart.Bar("Revenue by region", byRegion.Select(x => x.Region), byRegion.Select(x => x.Revenue));
```

Charts come back as data (`{ Type, Title, Labels, Series: [{ Name, Values }] }`); the host draws them, for
example with Chart.js.

### Try the demo

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
dotnet run --project samples/DemoHost
```

Open http://localhost:5280. The page is served from `localhost:5280` and the runner from `127.0.0.1:5281`,
two different sites, as a real deployment should be (see below). The first build publishes the runner,
which takes a minute.

### Use it in a web page

```html
<script src="sharp-interpreter.js"></script>
<script>
  const interpreter = new SharpInterpreter({ runnerUrl: 'https://runner.example-sandbox.net/' });
  const result = await interpreter.run(code, { 'sales.csv': fileOrText });
  // result: { Ok, Output, Error, Charts, CompileMs, RunMs }
</script>
```

Serve the runner (`dotnet publish src/SharpInterpreter.Runner -c Release`, then the `wwwroot` folder) like this:

- **On an origin of its own**, at its root. It must not share your app's origin (the iframe could then
  reach your app). Best is a domain of its own rather than a subdomain of your app's, so it never receives
  cookies set for your whole domain.
- **With this Content-Security-Policy on every file**, not just `index.html`: the worker gets its policy
  from its own response.

  ```
  default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; connect-src 'self'; worker-src 'self';
  frame-src 'none'; img-src 'none'; style-src 'none'; form-action 'none'; base-uri 'none';
  frame-ancestors https://your-app.example.com
  ```

- **Precompressed**: the publish output has a `.br` copy of every file; serve it with `Content-Encoding: br`
  when the browser accepts it.
- **From your own servers**, not a public CDN, if your users are behind a corporate firewall.

The iframe gets `sandbox="allow-scripts allow-same-origin"` (its own origin is needed to load the runtime;
being on another origin keeps it from reaching yours) and no popups, forms, top navigation or device access.

### Use it in a desktop app (WebView2)

Load the runner in a hidden WebView2 on a virtual host name (e.g. `https://runner.app.local`, served with
`WebResourceRequested` and the CSP above, without `frame-ancestors`), post `{ id, code, files, timeoutMs }`
with `PostWebMessageAsJson`, and listen for `WebMessageReceived`. Also cancel `NavigationStarting` for
anything outside that origin and turn off host objects.

### Security model

Scripts can call any .NET API (nothing is trimmed), including JavaScript through
`System.Runtime.InteropServices.JavaScript`. The boundary is where they run, not an API allow-list:

- **A Web Worker** in the runner page, on the runner's own origin. A worker has no DOM, no page to navigate
  (its `location` is read-only), no access to the host page, and no WebRTC.
- **No network**: once .NET has loaded, the worker removes `fetch`, `XMLHttpRequest`, `WebSocket`,
  `EventSource`, `WebTransport`, `sendBeacon` and `Worker` for good, and the CSP allows connections only to
  the runner's own server in any case.
- **No disk**: scripts see only the files the host passes in with each run.
- **Time**: a script that runs past the timeout is ended with its worker; the next run gets a fresh one.
- **The iframe** is sandboxed without popups, forms, top navigation or device access. If it ever navigates
  away, `sharp-interpreter.js` replaces it.

In tests, a script that tried to change `location`, reach `document`, `fetch` or `RTCPeerConnection`, or
put `fetch` back, got errors or `undefined`, and the iframe never navigated.

### Limits

- `Table.Read` reads CSV only. No NuGet packages: scripts have the base class library and the API above.
- Scripts that truly wait (e.g. `await Task.Delay`) aren't supported: the runtime has a single thread.
- After a timeout, the next run reloads .NET (about 3 seconds).
- One script at a time per interpreter.

### Project layout

```
src/
  SharpInterpreter.Script/   What scripts can call: Table, Row, Files, Chart, Out
  SharpInterpreter.Engine/   Compiles and runs scripts with Roslyn
  SharpInterpreter.Runner/   Runner page (runner.js) and the Web Worker that runs the engine (worker.js)
host/sharp-interpreter.js    Runs scripts from a web page through a hidden iframe
samples/DemoHost/            Demo: page and runner on two sites, with the headers the runner needs
tests/                       Engine tests (run on regular .NET)
```

```bash
dotnet test
```

---

<a name="繁體中文"></a>

## 繁體中文

在瀏覽器裡執行的 C# 程式碼直譯器。Roslyn 與 .NET runtime 編譯成 WebAssembly，在隱藏頁面的 Web Worker 中執行。AI 模型寫的（或使用者輸入的）腳本會在使用者自己的電腦上、瀏覽器的沙盒內編譯並執行：不需要執行程式碼的伺服器，不能連網，也碰不到宿主頁面交給它以外的檔案。

用途是聊天應用中的「Code Interpreter」功能：模型不再心算，而是寫一段簡短的 C# 腳本，讀取使用者的 CSV，用 LINQ 算出精確的數字並印出來，再畫出由宿主頁面呈現的圖表。

從 [Harness.WinUI](https://github.com/breezy89757/Harness.WinUI) 拆出來。

### 運作方式

```
宿主頁面 ── postMessage { id, code, files } ──▶ runner 頁面（隱藏 iframe，獨立 origin）
                                                 runner.js ──▶ Web Worker（worker.js）
                                                               .NET（WebAssembly）：Roslyn 在記憶體中
                                                               編譯 → Assembly.Load → 執行
宿主頁面 ◀── { type: 'result', id, result } ──── { Ok, Output, Error, Charts, CompileMs, RunMs }
```

- **Runner**（`src/SharpInterpreter.Runner`）：沒有 UI 的靜態網站。`runner.js` 把腳本轉給執行 .NET 的 Web Worker，腳本執行太久（預設 30 秒）就結束該 worker。可以放在 iframe 裡（和 `window.parent` 溝通），也可以放在 WebView2 裡（`chrome.webview`）。
- **Engine**（`src/SharpInterpreter.Engine`）：用 Roslyn 編譯腳本（頂層語句）、載入、執行，並擷取 `Console` 輸出。在一般 .NET 上也能跑，測試就是這樣跑的。
- **Script API**（`src/SharpInterpreter.Script`）：腳本可以呼叫的 API（見下方）。
- **Host client**（`host/sharp-interpreter.js`）：建立沙盒化的 iframe，一次執行一支腳本。

runner 經 brotli 壓縮後約 11 MB，第一次載入需要幾秒；第一次編譯因 Roslyn 暖機需要 1 到 2 秒，之後每次編譯加執行通常是 30 到 150 毫秒。腳本在 worker 中執行，所以執行期間宿主頁面不會卡住。

### 腳本 API

預設匯入：`System`、`System.Linq`、`System.Collections.Generic`、`System.Globalization`、`SharpInterpreter.Script`。

| API | |
|---|---|
| `Table.Read("sales.csv")` | CSV 轉成表格：`.Columns`、`.Rows`。第一列是欄名；支援 Excel 式的引號 |
| `row["欄名"]` | 文字。欄名不存在時丟出錯誤並列出所有欄名 |
| `row.Num("欄名")` | 轉 double；忽略千分位、`%`、`$`、`NT`；空白為 0 |
| `row.Date("欄名")` | 轉 `DateTime`（InvariantCulture） |
| `Files.ReadText("notes.txt")` | 檔案的文字內容（UTF-8） |
| `Out.Table(rows)` | 把物件（例如匿名型別）印成 Markdown 表格 |
| `Chart.Bar / Line / Pie` | `(title, labels, values)`，多數列：`(title, labels, new Series(name, values), …)` |

圖表以資料的形式回傳（`{ Type, Title, Labels, Series: [{ Name, Values }] }`），由宿主繪製，例如用 Chart.js。

### 試用 Demo

需要 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

```bash
dotnet run --project samples/DemoHost
```

打開 http://localhost:5280。頁面來自 `localhost:5280`，runner 來自 `127.0.0.1:5281`，是兩個不同的網站；正式部署也建議這樣（見下方）。第一次建置會發佈 runner，需要大約一分鐘。

### 在網頁中使用

```html
<script src="sharp-interpreter.js"></script>
<script>
  const interpreter = new SharpInterpreter({ runnerUrl: 'https://runner.example-sandbox.net/' });
  const result = await interpreter.run(code, { 'sales.csv': fileOrText });
</script>
```

runner（`dotnet publish src/SharpInterpreter.Runner -c Release` 產生的 `wwwroot` 資料夾）的部署方式：

- **放在獨立的 origin**，位於根目錄。不能和你的應用程式同一個 origin（否則 iframe 碰得到你的應用程式）。最好用獨立的網域，而不是應用程式的子網域，這樣它永遠不會收到設定給整個網域的 cookie。
- **每個檔案都加上上方英文段落列出的 Content-Security-Policy**，不只是 `index.html`：worker 的政策來自它自己的回應。`frame-ancestors` 填你的應用程式網址。
- **預先壓縮**：發佈結果中每個檔案都有 `.br` 版本，瀏覽器接受時以 `Content-Encoding: br` 提供。
- **放在自己的伺服器**，不要用公用 CDN，以免使用者在公司防火牆後面載入不到。

iframe 使用 `sandbox="allow-scripts allow-same-origin"`（需要自己的 origin 才能載入 runtime；位在另一個 origin，所以碰不到你的網站），不允許彈出視窗、表單、導覽上層頁面或存取裝置。

### 在桌面應用程式中使用（WebView2）

把 runner 載入隱藏的 WebView2，使用虛擬主機名稱（例如 `https://runner.app.local`，用 `WebResourceRequested` 提供檔案並加上同樣的 CSP，不需要 `frame-ancestors`），用 `PostWebMessageAsJson` 傳送 `{ id, code, files, timeoutMs }`，從 `WebMessageReceived` 接收結果。另外要在 `NavigationStarting` 取消所有離開該 origin 的導覽，並關閉 host objects。

### 安全模型

腳本可以呼叫任何 .NET API（沒有做 trimming），包括透過 `System.Runtime.InteropServices.JavaScript` 呼叫 JavaScript。安全邊界在於它在哪裡執行，而不是 API 白名單：

- **Web Worker**：在 runner 頁面、runner 自己的 origin 中執行。worker 沒有 DOM，沒有可以導覽的頁面（`location` 是唯讀的），碰不到宿主頁面，也沒有 WebRTC。
- **不能連網**：.NET 載入完成後，worker 會永久移除 `fetch`、`XMLHttpRequest`、`WebSocket`、`EventSource`、`WebTransport`、`sendBeacon` 和 `Worker`；CSP 本來也只允許連回 runner 自己的伺服器。
- **碰不到磁碟**：腳本只看得到宿主每次執行時交給它的檔案。
- **時間**：超過時限的腳本會連同它的 worker 一起結束，下一次執行使用新的 worker。
- **iframe**：沙盒化，不允許彈出視窗、表單、導覽上層頁面或存取裝置；萬一它導覽離開，`sharp-interpreter.js` 會換掉它。

實測中，腳本試圖修改 `location`、存取 `document`、`fetch`、`RTCPeerConnection`，或把 `fetch` 加回去，結果都是錯誤或 `undefined`，iframe 也從未被導覽。

### 限制

- `Table.Read` 只讀 CSV。不能用 NuGet 套件：腳本只有基礎類別庫和上述 API。
- 不支援真正需要等待的腳本（例如 `await Task.Delay`）：runtime 只有一條執行緒。
- 逾時之後，下一次執行要重新載入 .NET（約 3 秒）。
- 每個 interpreter 一次只執行一支腳本。

### 專案結構

```
src/
  SharpInterpreter.Script/   腳本可以呼叫的 API：Table、Row、Files、Chart、Out
  SharpInterpreter.Engine/   用 Roslyn 編譯並執行腳本
  SharpInterpreter.Runner/   runner 頁面（runner.js）與執行 engine 的 Web Worker（worker.js）
host/sharp-interpreter.js    在網頁中透過隱藏 iframe 執行腳本
samples/DemoHost/            Demo：頁面與 runner 分屬兩個網站，附 runner 需要的標頭
tests/                       Engine 測試（在一般 .NET 上執行）
```

---

## License

[MIT](LICENSE). Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
