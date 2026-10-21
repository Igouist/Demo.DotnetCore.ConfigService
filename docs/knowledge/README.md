# Knowledge Notes

這個目錄收錄導入與維護 Config Distribution POC 時需要理解的技術知識，例如 .NET 框架行為、容易誤判的設定語意，以及可重複使用的診斷資訊。

知識文章應以可驗證的事實和第一手資料來源為基礎。文章可以說明某項技術行為對本專案的影響及可行做法，但不記錄 `Accepted`、`Pending`、`Deferred` 等專案決策狀態；專案選擇及其狀態統一記錄於 [`decisions`](../decisions/) 或 [`adr`](../adr/) 目錄。

## Index

- [.NET Configuration 的陣列與多 Provider 合併行為](dotnet-configuration-array-merging.md)
- [呼叫端提供的啟動 Logger](caller-supplied-startup-logger.md)
- [HTTP、ASP.NET Core 8、System.Text.Json 與 HttpClient 的大小限制筆記](dotnet-http-size-limits.md)
- [.NET Framework 4.8 Receiver 相容性知識筆記](dotnet-framework-48-receiver-compatibility.md)
- [用外部 SQLite 工具查看 Central 資料庫](sqlite-external-tool-collation.md)
