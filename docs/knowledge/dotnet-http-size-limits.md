# HTTP、ASP.NET Core 8、System.Text.Json 與 HttpClient 的大小限制筆記

本文整理 Config Distribution POC 相關的 HTTP / .NET 傳輸與 JSON 大小限制事實，並說明這些限制與 POC 的關聯。本文只記錄可驗證的框架與協定行為、實作權衡與專案脈絡，不替專案做最終決策。

## 結論先行

- HTTP 規範沒有給 request body 或 response body 一個通用的全域 byte 上限；大小限制通常來自伺服器、反向代理、用戶端或應用程式自己的設定。RFC 9110 的 `413 Content Too Large` 只定義「伺服器不願意或不能處理這個大小的 request content」時的語意，沒有規定固定 threshold。[RFC 9110 §6.4](https://www.rfc-editor.org/rfc/rfc9110.html#section-6.4)、[RFC 9110 §8.6](https://www.rfc-editor.org/rfc/rfc9110.html#section-8.6)、[RFC 9110 §15.5.14](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.5.14)
- 對 ASP.NET Core 8 / Kestrel 而言，JSON request body 受一般 request body 限制控制；預設 `MaxRequestBodySize` 是 `30,000,000` bytes。它不是 JSON 專用限制，而是任何 request body 都適用的傳輸限制。[KestrelServerLimits.MaxRequestBodySize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxrequestbodysize?view=aspnetcore-8.0)
- Kestrel 對 request line 與 request headers 有明確預設值：`MaxRequestLineSize` 為 `8,192` bytes，`MaxRequestHeadersTotalSize` 為 `32,768` bytes。這些限制與 body 分開。[KestrelServerLimits.MaxRequestLineSize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxrequestlinesize?view=aspnetcore-8.0)、[KestrelServerLimits.MaxRequestHeadersTotalSize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxrequestheaderstotalsize?view=aspnetcore-8.0)
- Kestrel 沒有一個「整個 response body 最多幾 bytes」的預設總量限制；`MaxResponseBufferSize` 控制的是回應寫出時可先累積多少 buffer，超過後寫入會阻塞或回傳未完成的 Task，不是整體 response payload 上限。[KestrelServerLimits.MaxResponseBufferSize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxresponsebuffersize?view=aspnetcore-8.0)
- standalone `System.Text.Json` 的 `JsonSerializerOptions.MaxDepth` 預設是 `64`；若是 ASP.NET Core 間接使用 `System.Text.Json` 的 Controller / model-binding boundary，Microsoft 文件指出預設最大深度是 `32`。兩者都不是 payload byte-size cap。[JsonSerializerOptions.MaxDepth](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.maxdepth?view=net-8.0)、[Migrate from Newtonsoft.Json to System.Text.Json](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/migrate-from-newtonsoft?view=net-8.0)
- `HttpClient` 的 convenience `SendAsync` 路徑若未指定 `HttpCompletionOption`，預設會走 `ResponseContentRead`；若走這個路徑，`MaxResponseContentBufferSize` 的預設是 `2 GB`。改用 `ResponseHeadersRead` 時，操作會在 header 可用時完成，後續 body 由呼叫端以 stream 方式讀取。[HttpClient.SendAsync](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.sendasync?view=net-8.0)、[HttpCompletionOption](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-8.0)、[HttpClient.MaxResponseContentBufferSize](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.maxresponsecontentbuffersize?view=net-8.0)、[dotnet/runtime `HttpClient.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Http/src/System/Net/Http/HttpClient.cs)
- SQLite 對單一 string / BLOB 的 compile-time 預設上限是 `1,000,000,000` bytes，但目前實作可支援的實際上限是 `2^31-3`，也就是 `2,147,483,645` bytes；這是資料庫實作上限，不是適合拿來當 POC HTTP 或 domain contract 的業務限制。[SQLite Limits](https://www.sqlite.org/limits.html)、[SQLITE_LIMIT_LENGTH](https://www.sqlite.org/c3ref/c_limit_attached.html)

## HTTP 協定本身是否有通用 body 大小上限

就 HTTP Semantics 規範而言，我沒有找到 request body 或 response body 的通用、固定 byte 上限。RFC 9110 定義的是 content 的語意與訊息 framing，例如 request content 的意義由 method semantics 決定，而 `Content-Length` 只是描述 content 大小的欄位，不是跨實作的全域最大值。[RFC 9110 §6.4.1](https://www.rfc-editor.org/rfc/rfc9110.html#section-6.4.1)、[RFC 9110 §8.6](https://www.rfc-editor.org/rfc/rfc9110.html#section-8.6)

相對地，RFC 9110 對 header field section 明確說了「HTTP does not place a predefined limit」；也就是 header line、field value、整體 header/trailer section 本身沒有規範級固定長度，而接收端可自行決定願意處理多大，超過時應回適當的 `4xx`。[RFC 9110 §5.4](https://www.rfc-editor.org/rfc/rfc9110.html#section-5.4)

RFC 9110 另外定義了 `413 Content Too Large`：當 request content 大於伺服器願意或能處理的大小時可使用這個狀態碼。但這個狀態碼只定義語意，不提供一個協定層固定門檻。[RFC 9110 §15.5.14](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.5.14)

因此，如果 POC 想限制 request/response body，來源不會是 HTTP 協定本身，而會是 Kestrel、反向代理、client buffering 策略，或專案自己定義的 contract。

## ASP.NET Core 8 / Kestrel 的預設限制

### JSON request body

Kestrel 的 `MaxRequestBodySize` 是「任何 request body」的最大允許大小，預設 `30,000,000` bytes，約 `28.6 MB`。當它設成 `null` 時代表 unlimited，且可透過 `IHttpMaxRequestBodySizeFeature` 在 per-request 覆寫；但必須在開始讀 body 之前設定。[KestrelServerLimits.MaxRequestBodySize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxrequestbodysize?view=aspnetcore-8.0)、[Configure options for the ASP.NET Core Kestrel web server](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-8.0#maximum-request-body-size)

這代表 ASP.NET Core 8 對 JSON request body 沒有另一個獨立於 request body 之外的 Kestrel 預設 byte limit；JSON 只是其中一種 media type。

### Request line

`MaxRequestLineSize` 預設是 `8,192` bytes。對 HTTP/2 與 HTTP/3，這個值衡量的是必要 pseudo-headers `:method`、`:scheme`、`:authority`、`:path` 的總大小。[KestrelServerLimits.MaxRequestLineSize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxrequestlinesize?view=aspnetcore-8.0)

### Request headers

`MaxRequestHeadersTotalSize` 預設是 `32,768` bytes；Kestrel 的安全性文件也把它描述為所有 request headers 合計大小上限，超過時會回 `431 Request Header Fields Too Large`。[KestrelServerLimits.MaxRequestHeadersTotalSize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxrequestheaderstotalsize?view=aspnetcore-8.0)、[Kestrel security considerations](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/security-considerations?view=aspnetcore-10.0#configurable-limits)

同一份 Kestrel 文件另外列出 `MaxRequestHeaderCount` 預設為 `100`。除了 header 總大小，header 數量也是另一項限制；若未來 POC 加入大量自訂 header，也應納入評估。[KestrelServerLimits](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits?view=aspnetcore-8.0)

### Response body

Kestrel 這裡容易混淆的不是「有沒有 response body limit」，而是「`MaxResponseBufferSize` 不是 total response size limit」。官方 API 文件明確寫的是：它控制 response buffer 在寫入呼叫開始阻塞前可累積的最大大小，預設 `65,536` bytes (`64 KB`)；設成 `null` 代表 buffer unlimited，設成 `0` 代表每次寫入都要等 flush。[KestrelServerLimits.MaxResponseBufferSize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxresponsebuffersize?view=aspnetcore-8.0)

因此，以 Kestrel 預設值來說，POC 的 response body 沒有一個對應於 `MaxRequestBodySize` 的總量上限；真正會先遇到的通常是 client 端是否緩衝整個 response、反向代理限制、或應用程式自己的回應 contract。

## System.Text.Json 的預設限制

### 有文件明確記錄的預設：最大深度 64

standalone `System.Text.Json` 中，`JsonSerializerOptions.MaxDepth` 的預設值 `0` 代表實際最大深度 `64`；超過會丟 `JsonException`。`JsonDocumentOptions.MaxDepth` 與 `JsonReaderOptions.MaxDepth` 也採相同預設語意。[JsonSerializerOptions.MaxDepth](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.maxdepth?view=net-8.0)、[JsonDocumentOptions.MaxDepth](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsondocumentoptions.maxdepth?view=net-8.0)、[JsonReaderOptions.MaxDepth](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonreaderoptions.maxdepth?view=net-8.0)

`System.Text.Json` 的 threat model 也把 deeply nested JSON 視為 DoS 風險，並說明預設 `MaxDepth = 64` 是對此風險的緩解。[System.Text.Json Threat Model](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Text.Json/docs/ThreatModel.md)

### ASP.NET Core 間接使用 `System.Text.Json` 時的預設：最大深度 32

Microsoft 的 migration 文件明確區分：若是直接使用 `System.Text.Json`，預設最大深度是 `64`；若是透過 ASP.NET Core 間接使用 `System.Text.Json`，預設最大深度是 `32`，並且與 model binding 的預設一致。[Migrate from Newtonsoft.Json to System.Text.Json](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/migrate-from-newtonsoft?view=net-8.0)

這個差異對本 POC 的 Controller boundary 比較 relevant，因為 Controllers 接收 request DTO 時是走 ASP.NET Core 的 JSON/input formatting 邊界，而不是每次都由專案直接 new 一個 standalone `JsonSerializerOptions`。不過這個 `32` 仍然只是巢狀深度上限，不是 payload byte-size cap；而且本 POC 的 request/response DTO 結構很淺，實務上不太可能靠近這個深度，所以它不足以構成專案真正想要的 size contract。

### 我沒有找到通用 payload byte-size 的內建預設上限

官方 API 文件把 `JsonDocument.Parse(...)` 定義為解析一段 `string`、`Stream`、`ReadOnlyMemory<byte>` 或 `ReadOnlySequence<byte>` 成一個 JSON 值；`JsonSerializer.DeserializeAsync(...)` 也明確說會把 `Stream` 或 `PipeReader` 讀到 completion。這些文件有描述輸入型別與讀取方式，但沒有像 `MaxDepth` 那樣為 payload byte-size 指定一個通用預設上限。[JsonDocument.Parse](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsondocument.parse?view=net-8.0)、[JsonSerializer.DeserializeAsync](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializer.deserializeasync?view=net-8.0)

因此，若只問 `System.Text.Json` 本身的預設 byte ceiling，我能根據官方資料做的最保守結論是：文件有明確記錄深度上限，但沒有明確記錄一個通用 payload byte-size 預設值。無論是 standalone `64` 還是 ASP.NET Core boundary `32`，都不是 payload byte-size cap。實際可接受大小仍會受到外層傳輸限制、呼叫端 buffering 策略與可用記憶體影響。

## HttpClient 的 response buffering / streaming 預設

### 預設 `SendAsync` 路徑是讀完整個 response content

`HttpClient.SendAsync(HttpRequestMessage)` 與 `SendAsync(HttpRequestMessage, CancellationToken)` 若未指定 `HttpCompletionOption`，在 .NET 官方 source 中都會委派到 `DefaultCompletionOption`，而這個常數就是 `HttpCompletionOption.ResponseContentRead`。當 `completionOption == ResponseContentRead` 時，`HttpClient` 會把 response content 載入 buffer 後才完成該操作。[HttpClient.SendAsync](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.sendasync?view=net-8.0)、[dotnet/runtime `HttpClient.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Http/src/System/Net/Http/HttpClient.cs)

`HttpCompletionOption` 的官方文件則說 `ResponseContentRead` 是 enum 的預設值，而 `ResponseHeadersRead` 會在 response 可用且 header 讀完時就完成，content 尚未讀取完畢。[HttpCompletionOption](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-8.0)

`GetAsync` 仍可作一般補充，因為它最後也是建立 GET request 後走相同 completion-option 模型；但對本 POC 的 Resolve 來說，HTTP 方法是 `POST`，所以用 `SendAsync` 來描述預設 buffering 行為比較貼近實際呼叫路徑。

### 預設緩衝上限是 2 GB

當 `HttpClient` 走 response buffering 路徑時，`MaxResponseContentBufferSize` 的預設值是 `2 gigabytes`；官方文件也說超過此值時會拋例外。[HttpClient.MaxResponseContentBufferSize](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.maxresponsecontentbuffersize?view=net-8.0)

### 與串流模式的差別

若呼叫端改用 `ResponseHeadersRead`，`SendAsync` 本身就不會等整個 body 先讀完，後續內容通常要透過 `HttpContent.ReadAsStreamAsync()` 或 JSON 的 stream-based API 自行處理。這會把限制焦點從 `HttpClient` 內建 buffering 上限，轉成應用程式如何消費 stream、是否逐步處理、以及是否額外自訂上限。[HttpClient.SendAsync](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.sendasync?view=net-8.0)、[HttpCompletionOption](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-8.0)、[JsonSerializer.DeserializeAsync](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializer.deserializeasync?view=net-8.0)

對 .NET 8 而言，框架另外新增了 `GetFromJsonAsAsyncEnumerable` / `ReadFromJsonAsAsyncEnumerable` 這類 streaming deserialization API，代表官方也把「大型 JSON payload 不一定要整包 buffering」視為一個獨立使用情境。[.NET 8 runtime 新功能：Streaming deserialization APIs](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-8/runtime#streaming-deserialization-apis)、[HttpContentJsonExtensions](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.json.httpcontentjsonextensions?view=net-8.0)

## SQLite 上限，以及它與本 POC 的關聯

SQLite 官方 limits 文件說明：單一 string 或 BLOB 的 compile-time 預設上限由 `SQLITE_MAX_LENGTH` 定義，預設值是 `1,000,000,000` bytes；但 current implementation 實際只支援到 `2^31-3`，也就是 `2,147,483,645` bytes。`SQLITE_LIMIT_LENGTH` 也是可在 connection 上下調的 run-time limit 類別。[SQLite Limits](https://www.sqlite.org/limits.html)、[Run-Time Limit Categories / SQLITE_LIMIT_LENGTH](https://www.sqlite.org/c3ref/c_limit_attached.html)

這個上限與 POC 的 HTTP / JSON 傳輸限制不是同一層概念：

- 它只在資料真的進到 SQLite 的 `TEXT` / `BLOB` / row 大小時才 relevant。
- 它遠高於一般設定系統應該接受的 payload 尺寸，無法代替 API contract 的語意限制。
- 它也不會幫 HTTP 邊界自動阻擋過大的 request body；在那之前，Kestrel、ASP.NET Core model binding、System.Text.Json 與應用程式驗證就已經先處理了。

所以，SQLite 上限對這個 POC 比較像一個遙遠的實作 ceiling，而不是應直接採用的 domain boundary。只有當專案正在討論「單一 Configuration Value 能不能大到接近文件、憑證或二進位 payload」時，SQLite 的上限才會進入考量。

## 對 Config Distribution POC 的關聯

根據本 repo 的 POC 記錄，這個系統有幾個已知特徵：

- `Resolve` 使用 `POST`，就是為了在 request body 內傳送一個 Environment 與可能不小的 Key List，而不是把它塞進 query string。[POC-022](../decisions/poc-decision-log.md)
- Management `Set` contract 為單一 `environment`、`key`、`value`，而且 response 不會回傳 `value`；這降低了 response 反射大型 value 的風險，但 request 仍可能攜帶大型 `value`。[POC-025](../decisions/poc-decision-log.md)
- Management current-state 與 history 使用 `GET`，並以 query parameters 接收 Environment 與 Configuration Key。[POC-027](../decisions/poc-decision-log.md) 因此，如果 `Set` 的 JSON body 接受一個極長的識別字，之後的 GET 可能先撞到 Kestrel 或反向代理的 request-line / URI 限制而無法查回。Environment 與 Key 的 semantic length limit 因而也具有「確保已建立 Entry 仍可由既有 Management contract 定址」的功能性價值，不只是 DoS 防護。
- POC-050 定義單一 Configuration Value 的大小邊界。專案是否另外限制 HTTP request body、Environment / Key 長度、Resolve Key count、aggregate Key bytes 或 total Distribution response，則由 POC-053 記錄；本文不重述或替代該專案選擇。[POC-050 與 POC-053](../decisions/poc-decision-log.md)

在這個脈絡下，評估自訂限制時可以拆成兩層來看。以下只整理兩類限制各自能解決的問題，不表示 POC 必須啟用它們。

### 1. 未來若考慮 Semantic limits，它們能解決什麼

Kestrel 的 `30,000,000` byte request body 預設值無法回答下列 domain 與容量問題：

- Environment 名稱可以多長，並不是 HTTP 協定或 Kestrel 能替 domain 回答的問題。
- Configuration Key 可以多長、含幾段、總 aggregate bytes 多大，也不是單靠 transport body size 能表達。
- Resolve 一次可帶幾個 key，影響的不只 request body bytes，還會影響 server 端驗證、查詢、回應序列化、client buffering，以及啟動時整批載入的可預期成本。
- 即使單次 Management `Set` 的 `value` 有獨立上限，Distribution `Resolve` 的 response 仍可能因 key 數量、key 長度、與 value 組合而膨脹。

因此，若專案日後加入 semantic limits，它們的價值會是定義 domain contract 與資源成本模型，而不只是防止 request 太大。是否需要這些保證，仍應由專案範圍與實測資料決定。

### 2. 未來若考慮 Transport body limits，它們能解決什麼

Transport limit 與 semantic limit 的作用不同：

- 它能在 request 尚未進入完整 JSON 反序列化與 domain 驗證前，先阻擋明顯過大的 payload。
- 它可以限制惡意或錯誤 caller 送入大量無意義 JSON 結構、重複欄位、或與 domain 無關的大型字串，避免只靠後段驗證承擔成本。
- 它與 semantic limit 的單位不同：semantic limit 多半是 key count、字元數、aggregate bytes、單值大小；transport limit 是整包 request bytes，必須替 JSON 包裝成本留餘裕。

Transport limit 本身不能替代 semantic limit，因為兩個完全不同的 payload 可以有相近 body bytes，卻對 domain 與回應成本造成完全不同的影響。反過來，是否值得在 framework default 之外再加一層 transport limit，也取決於威脅模型、實際 payload 與 POC 或正式系統的範圍。

### 3. 各 framework default 與本專案輸入的關係

- `MaxRequestLineSize = 8 KB`：Resolve 與 Management commands 的主要輸入放在 JSON body；Management current-state 與 history 則把 Environment 和 Key 放在 query string。因此 8 KB 不是整個 API 共用的資料大小上限，也不宜直接當成可使用的 domain 長度。代理層可能有不同或更小的 URI 限制，URL percent-encoding 也會增加傳輸長度。
- `MaxRequestHeadersTotalSize = 32 KB`：若 POC 維持簡單 header 集合，這通常也不是主要容量邊界；只有加入大型認證 header、很多自訂 header 或代理層轉發 header 時才更 relevant。
- SQLite 的 `1,000,000,000` byte compile-time 預設與 `2,147,483,645` byte current-implementation ceiling：對設定系統來說都過高，較像最後一道實作極限，不適合直接拿來當 API 或 domain 設計依據。

## 可直接帶回設計討論的事實

- HTTP 規範本身不會替 POC 決定 request/response body 上限；這必須由實作層與 contract 層自行定義。
- ASP.NET Core 8 / Kestrel 的預設 request body 限制是約 `28.6 MB`，遠大於多數設定型 API 的正常需求。
- Kestrel 對 response 沒有對應的 total body default cap；若不自行限制，response 尺寸主要受資料量與 client 消費方式影響。
- `System.Text.Json` 官方文件有明確的預設 depth limit，但沒有對所有 JSON payload 提供一個通用 byte-size 預設上限；而且 standalone `64` 與 ASP.NET Core boundary `32` 都是深度限制，不是大小契約。
- `HttpClient` 預設 `SendAsync` 路徑傾向把整個 response 讀完才完成，且預設 buffer 上限高達 `2 GB`；這讓「Distribution response 是否應有自己的上限」不只是 server concern，也直接影響 consumer 端的記憶體與啟動路徑。

## 第一手資料

- [RFC 9110 §5.4 Field Limits](https://www.rfc-editor.org/rfc/rfc9110.html#section-5.4)
- [RFC 9110 §6.4 Content](https://www.rfc-editor.org/rfc/rfc9110.html#section-6.4)
- [RFC 9110 §6.4.1 Content Semantics](https://www.rfc-editor.org/rfc/rfc9110.html#section-6.4.1)
- [RFC 9110 §15.5.14 Content Too Large](https://www.rfc-editor.org/rfc/rfc9110.html#section-15.5.14)
- [RFC 9110 §8.6 Content-Length](https://www.rfc-editor.org/rfc/rfc9110.html#section-8.6)
- [KestrelServerLimits.MaxRequestBodySize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxrequestbodysize?view=aspnetcore-8.0)
- [KestrelServerLimits.MaxRequestLineSize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxrequestlinesize?view=aspnetcore-8.0)
- [KestrelServerLimits.MaxRequestHeadersTotalSize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxrequestheaderstotalsize?view=aspnetcore-8.0)
- [KestrelServerLimits.MaxResponseBufferSize](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits.maxresponsebuffersize?view=aspnetcore-8.0)
- [KestrelServerLimits](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.server.kestrel.core.kestrelserverlimits?view=aspnetcore-8.0)
- [Configure options for the ASP.NET Core Kestrel web server](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/options?view=aspnetcore-8.0)
- [Kestrel security considerations](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/security-considerations?view=aspnetcore-10.0#configurable-limits)
- [JsonReaderOptions.MaxDepth](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonreaderoptions.maxdepth?view=net-8.0)
- [JsonDocumentOptions.MaxDepth](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsondocumentoptions.maxdepth?view=net-8.0)
- [JsonSerializerOptions.MaxDepth](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializeroptions.maxdepth?view=net-8.0)
- [JsonDocument.Parse](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsondocument.parse?view=net-8.0)
- [JsonSerializer.DeserializeAsync](https://learn.microsoft.com/en-us/dotnet/api/system.text.json.jsonserializer.deserializeasync?view=net-8.0)
- [Migrate from Newtonsoft.Json to System.Text.Json](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/migrate-from-newtonsoft?view=net-8.0)
- [System.Text.Json Threat Model](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Text.Json/docs/ThreatModel.md)
- [HttpClient.SendAsync](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.sendasync?view=net-8.0)
- [HttpCompletionOption](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption?view=net-8.0)
- [HttpClient.MaxResponseContentBufferSize](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclient.maxresponsecontentbuffersize?view=net-8.0)
- [dotnet/runtime `HttpClient.cs`](https://github.com/dotnet/runtime/blob/main/src/libraries/System.Net.Http/src/System/Net/Http/HttpClient.cs)
- [.NET 8 runtime 新功能：Streaming deserialization APIs](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-8/runtime#streaming-deserialization-apis)
- [HttpContentJsonExtensions](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.json.httpcontentjsonextensions?view=net-8.0)
- [SQLite Limits](https://www.sqlite.org/limits.html)
- [Run-Time Limit Categories / SQLITE_LIMIT_LENGTH](https://www.sqlite.org/c3ref/c_limit_attached.html)
