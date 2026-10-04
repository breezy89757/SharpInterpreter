# SharpInterpreter

[English](#english) | [繁體中文](#繁體中文)

<a name="english"></a>

## English

A C# code interpreter that runs in the browser. Roslyn and the .NET runtime are compiled to WebAssembly and
run in a hidden page, so scripts an AI model writes (or a user types) are compiled and run on the user's
own device, inside the browser's sandbox: no server to run code on, no network, and no disk beyond the
files the host page passes in.

It is meant for "code interpreter" features in chat apps: instead of doing arithmetic in its head, the
model writes a short C# script that reads the user's CSV, computes exact numbers with LINQ, prints them,
and draws charts that the host page renders.

Extracted from [Harness.WinUI](https://github.com/breezy89757/Harness.WinUI), where it runs in a hidden WebView2.

### How it works

```
host page ── postMessage { id, code, files } ──▶ runner page (hidden iframe, its own site)
                                                   runner.js → .NET (WebAssembly)
                                                   Roslyn compiles in memory → Assembly.Load → run
host page ◀── { type: 'result', id, result } ──── { Ok, Output, Error, Charts, CompileMs, RunMs }
```

- **Runner** (`src/SharpInterpreter.Runner`): a Blazor WebAssembly static site with no UI. `runner.js`
  receives scripts and returns results; it works in an iframe (talks to `window.parent`) or in a WebView2
  (`chrome.webview`).
- **Engine** (`src/SharpInterpreter.Engine`): compiles a script with Roslyn (top-level statements), loads it,
  runs it and captures `Console` output. Also runs on regular .NET, which is how the tests run.
- **Script API** (`src/SharpInterpreter.Script`): what scripts can call (below).
- **Host client** (`host/sharp-interpreter.js`): creates the sandboxed iframe, runs one script at a time,
  and throws the iframe away when a script runs too long (30 seconds by default).

Startup takes about half a second, the first compile 1 to 2 seconds while Roslyn warms up, and after that
a compile plus run typically takes 30 to 150 ms. The runner is about 12 MB brotli-compressed.

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
two different sites, which is how a real deployment must be set up (see below). The first build publishes
the runner, which takes a minute.

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

- **On a site of its own**, at the root of its origin: a different registrable domain from your app, not a
  subdomain of it. Browsers run same-site frames on the same thread as the page, so a long-running script
  would freeze your app and the timeout could never fire.
- **With this Content-Security-Policy on `index.html`** (`sha256-…` is the hash of the inline import map in
  the published `index.html`, after converting CRLF to LF; `samples/DemoHost/Program.cs` computes it):

  ```
  default-src 'none'; script-src 'self' 'wasm-unsafe-eval' 'sha256-…'; connect-src 'self';
  frame-src 'none'; worker-src 'none'; img-src 'none'; style-src 'none'; form-action 'none';
  base-uri 'self'; frame-ancestors https://your-app.example.com
  ```

- **Precompressed**: the publish output has a `.br` copy of every file; serve it with `Content-Encoding: br`
  when the browser accepts it (about 12 MB instead of about 40).
- **From your own servers**, not a public CDN, if your users are behind a corporate firewall.

The iframe gets `sandbox="allow-scripts allow-same-origin"` (its own origin is needed to load the runtime;
being on another site keeps it from reaching yours) and no popups, forms, top navigation or device access.

### Use it in a desktop app (WebView2)

Load the runner in a hidden WebView2 on a virtual host name (e.g. `https://runner.app.local`, served with
`WebResourceRequested` and the CSP above, without `frame-ancestors`), and post messages with
`PostWebMessageAsJson`. Also cancel `NavigationStarting` for anything outside that origin, remove WebRTC with
`AddScriptToExecuteOnDocumentCreatedAsync`, turn off host objects, and replace the WebView when a script times
out. [Harness.WinUI's ScriptRunnerHost](https://github.com/breezy89757/Harness.WinUI) does all of this.

### Security model

Scripts run inside the browser's sandbox, on the runner's own site: they can't read the host page, the
user's disk, or cookies of other sites. The CSP allows no connections except to the runner's own server,
and no frames, images, styles or forms. WebRTC, which CSP doesn't cover, is removed before any .NET code
runs. A script that runs too long is ended by discarding the whole page.

Scripts can call any .NET API (nothing is trimmed), including the page's JavaScript through
`System.Runtime.InteropServices.JavaScript`. The boundary is the browser sandbox, not an API allow-list.

**Known issue.** Because scripts can reach the page's JavaScript, a script can navigate its own iframe to
another site, carrying data in the URL; a browser offers no way to stop a frame navigating itself.
`sharp-interpreter.js` throws away a frame that navigated, but only after the request has been sent.
(A WebView2 host blocks it with `NavigationStarting`.) The fix is to run the .NET runtime in a Web Worker
inside the runner page: a worker has no location to change, no DOM and no WebRTC, and the page could end a
long script with `worker.terminate()`.

### Limits

- `Table.Read` reads CSV only. No NuGet packages: scripts have the base class library and the API above.
- Scripts that truly wait (e.g. `await Task.Delay`) aren't supported: the runtime has a single thread.
- One script at a time per interpreter.

### Project layout

```
src/
  SharpInterpreter.Script/   What scripts can call: Table, Row, Files, Chart, Out
  SharpInterpreter.Engine/   Compiles and runs scripts with Roslyn
  SharpInterpreter.Runner/   Blazor WebAssembly page that hosts the engine (runner.js)
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

在瀏覽器裡執行的 C# 程式碼直譯器。Roslyn 與 .NET runtime 編譯成 WebAssembly，在隱藏的頁面中執行。AI 模型寫的（或使用者輸入的）腳本會在使用者自己的電腦上、瀏覽器的沙盒內編譯並執行：不需要執行程式碼的伺服器，不能連網，也碰不到宿主頁面交給它以外的檔案。

用途是聊天應用中的「Code Interpreter」功能：模型不再心算，而是寫一段簡短的 C# 腳本，讀取使用者的 CSV，用 LINQ 算出精確的數字並印出來，再畫出由宿主頁面呈現的圖表。

從 [Harness.WinUI](https://github.com/breezy89757/Harness.WinUI) 拆出來；在那裡它跑在隱藏的 WebView2 中。

### 運作方式

```
宿主頁面 ── postMessage { id, code, files } ──▶ runner 頁面（隱藏 iframe，獨立網站）
                                                 runner.js → .NET（WebAssembly）
                                                 Roslyn 在記憶體中編譯 → Assembly.Load → 執行
宿主頁面 ◀── { type: 'result', id, result } ──── { Ok, Output, Error, Charts, CompileMs, RunMs }
```

- **Runner**（`src/SharpInterpreter.Runner`）：沒有 UI 的 Blazor WebAssembly 靜態網站。`runner.js` 接收腳本、回傳結果；可以放在 iframe 裡（和 `window.parent` 溝通），也可以放在 WebView2 裡（`chrome.webview`）。
- **Engine**（`src/SharpInterpreter.Engine`）：用 Roslyn 編譯腳本（頂層語句）、載入、執行，並擷取 `Console` 輸出。在一般 .NET 上也能跑，測試就是這樣跑的。
- **Script API**（`src/SharpInterpreter.Script`）：腳本可以呼叫的 API（見下方）。
- **Host client**（`host/sharp-interpreter.js`）：建立沙盒化的 iframe，一次執行一支腳本；腳本執行太久（預設 30 秒）就把整個 iframe 丟掉。

啟動約 0.5 秒；第一次編譯因 Roslyn 暖機需要 1 到 2 秒，之後每次編譯加執行通常是 30 到 150 毫秒。runner 經 brotli 壓縮後約 12 MB。

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

打開 http://localhost:5280。頁面來自 `localhost:5280`，runner 來自 `127.0.0.1:5281`，是兩個不同的網站；正式部署也必須這樣（見下方）。第一次建置會發佈 runner，需要大約一分鐘。

### 在網頁中使用

```html
<script src="sharp-interpreter.js"></script>
<script>
  const interpreter = new SharpInterpreter({ runnerUrl: 'https://runner.example-sandbox.net/' });
  const result = await interpreter.run(code, { 'sales.csv': fileOrText });
</script>
```

runner（`dotnet publish src/SharpInterpreter.Runner -c Release` 產生的 `wwwroot` 資料夾）的部署方式：

- **放在獨立的網站**，位於該 origin 的根目錄：要用和你的應用程式不同的註冊網域，不能是它的子網域。瀏覽器會讓同一網站的 frame 和頁面共用一條執行緒，執行很久的腳本會讓你的應用程式卡住，逾時也永遠不會觸發。
- **`index.html` 加上上方英文段落列出的 Content-Security-Policy**（`sha256-…` 是發佈後 `index.html` 中 inline import map 的雜湊，計算前先把 CRLF 轉成 LF；`samples/DemoHost/Program.cs` 有計算方式），`frame-ancestors` 填你的應用程式網址。
- **預先壓縮**：發佈結果中每個檔案都有 `.br` 版本，瀏覽器接受時以 `Content-Encoding: br` 提供（約 12 MB，而不是約 40 MB）。
- **放在自己的伺服器**，不要用公用 CDN，以免使用者在公司防火牆後面載入不到。

iframe 使用 `sandbox="allow-scripts allow-same-origin"`（需要自己的 origin 才能載入 runtime；位在另一個網站，所以碰不到你的網站），不允許彈出視窗、表單、導覽上層頁面或存取裝置。

### 在桌面應用程式中使用（WebView2）

把 runner 載入隱藏的 WebView2，使用虛擬主機名稱（例如 `https://runner.app.local`，用 `WebResourceRequested` 提供檔案並加上同樣的 CSP，不需要 `frame-ancestors`），用 `PostWebMessageAsJson` 傳訊息。另外要在 `NavigationStarting` 取消所有離開該 origin 的導覽、用 `AddScriptToExecuteOnDocumentCreatedAsync` 移除 WebRTC、關閉 host objects，並在腳本逾時時換掉整個 WebView。[Harness.WinUI 的 ScriptRunnerHost](https://github.com/breezy89757/Harness.WinUI) 都有實作。

### 安全模型

腳本在瀏覽器沙盒內、runner 自己的網站上執行：讀不到宿主頁面、使用者的磁碟或其他網站的 cookie。CSP 只允許連回 runner 自己的伺服器，不允許 frame、圖片、樣式或表單。CSP 管不到的 WebRTC 會在任何 .NET 程式碼執行前移除。執行太久的腳本，會連同整個頁面一起被丟掉。

腳本可以呼叫任何 .NET API（沒有做 trimming），包括透過 `System.Runtime.InteropServices.JavaScript` 操作頁面的 JavaScript。安全邊界是瀏覽器沙盒，而不是 API 白名單。

**已知問題**：由於腳本碰得到頁面的 JavaScript，它可以把自己所在的 iframe 導向其他網站，並把資料放在網址裡；瀏覽器沒有辦法阻止 frame 導覽自己。`sharp-interpreter.js` 會丟掉導覽離開的 frame，但那時請求已經送出。（WebView2 宿主可以用 `NavigationStarting` 擋下。）解法是在 runner 頁面裡用 Web Worker 執行 .NET runtime：worker 沒有可以改的 location，沒有 DOM，也沒有 WebRTC，頁面還可以用 `worker.terminate()` 結束執行太久的腳本。

### 限制

- `Table.Read` 只讀 CSV。不能用 NuGet 套件：腳本只有基礎類別庫和上述 API。
- 不支援真正需要等待的腳本（例如 `await Task.Delay`）：runtime 只有一條執行緒。
- 每個 interpreter 一次只執行一支腳本。

### 專案結構

```
src/
  SharpInterpreter.Script/   腳本可以呼叫的 API：Table、Row、Files、Chart、Out
  SharpInterpreter.Engine/   用 Roslyn 編譯並執行腳本
  SharpInterpreter.Runner/   承載 engine 的 Blazor WebAssembly 頁面（runner.js）
host/sharp-interpreter.js    在網頁中透過隱藏 iframe 執行腳本
samples/DemoHost/            Demo：頁面與 runner 分屬兩個網站，附 runner 需要的標頭
tests/                       Engine 測試（在一般 .NET 上執行）
```

---

## License

[MIT](LICENSE). Third-party components: [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
