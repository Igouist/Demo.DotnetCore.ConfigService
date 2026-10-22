namespace Demo.DotnetCore.ConfigService.CentralHost;

// Shared Swagger UI text; the HTTP contract itself is defined in docs/design/config-distribution-poc.md §4.
internal static class OpenApiDescriptions
{
    public const string Json = "application/json";
    public const string Problem = "application/problem+json";

    public const string EnvironmentParameter = "中央環境名稱，例如 `UAT`；不分大小寫，前後不可有空白。";
    public const string KeyParameter = "設定鍵，以 `:` 分隔階層，例如 `Demo:Message`；不分大小寫。";

    public const string ChangeAttribution =
        "`changedBy` 必填，填寫變更者（最多 100 字）；`reason` 選填，填寫變更原因（最多 500 字）。兩者會去除前後空白、不可含控制字元，並出現在版本歷史中，請勿填入機密。`changedBy` 目前是自行填寫的文字，不是驗證過的身分。";

    public const string ValidationFailed =
        "請求格式或欄位不正確（`code`: `validation_failed`）。`errors` 列出每個欄位的問題，例如缺少欄位、未知欄位或設定鍵格式錯誤。";
    public const string NotFound = "這個中央環境下從未建立過此設定鍵（`code`: `configuration_not_found`）。";
    public const string AlreadyRemoved = "設定鍵目前已是移除狀態，不能再移除一次（`code`: `configuration_already_removed`）。";
    public const string ServerError =
        "Central 內部錯誤（`code`: `persistence_failure` 或 `internal_error`）。用回應的 `traceId` 在 Central 日誌找原因。";
    public const string Busy = "資料庫暫時忙碌（`code`: `persistence_busy`），稍後重試即可。";
}
