---
status: accepted
date: 2026-10-22
---

# Client 容忍未知回應欄位，Central 嚴格驗證請求

Central 可以在 v1 回應中向後相容地新增欄位。Client 解析 Resolve 回應時略過不認識的最上層欄位，不讓啟動失敗，並在每次啟動記錄一筆 Warning，只列出欄位名稱、不列值，提醒維護人員升級 Client 套件。`values` 與 `missingKeys` 仍須各出現一次且格式正確，`values` 內的重複設定鍵仍會拒絕；容忍只放寬「多出來的欄位」，不放寬既有資料的正確性。

反方向維持嚴格：Central 繼續以 `unknown_property` 拒絕請求中不認識的欄位。Client 日後需要傳送新資訊時，改用不會讓舊版 Central 拒絕的方式，例如選填標頭。

## Considered Options

- Client 拒絕未知的最上層回應欄位（原設計文件第 15 節第 9 點）。
- Client 容忍未知回應欄位並記錄 Warning；Central 嚴格驗證請求。
- Client 容忍未知回應欄位且完全不記錄。
- 回應維持嚴格，每次擴充都改用新的版本化路由（例如 `/api/v2/...`）。

## Consequences

- 一個 Central 服務多個使用端，Central 擴充回應時不必強迫所有使用端同步升級。[#21](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/21) 的 `revision` 是第一個受益的欄位。
- Central 比 Client 新時，Client 每次啟動都會出現這筆 Warning，直到升級為止；這是預期行為，目的就是提醒升級。
- 新增的回應欄位只能是「舊 Client 略過也不會出錯」的資訊。若某個變更要求 Client 一定要理解才能正確運作，就必須改用新的版本化路由，不能只加欄位。
- 請求端嚴格驗證保留明確的錯誤回報，方便除錯；代價是 Client 不能直接在請求本文加欄位給舊版 Central。
- 這條規則一旦有使用端部署就很難收回，因此在 POC 成為正式實作地基之前定案。

實作與設計文件第 8.2 節、第 15 節第 9 點的修訂由 [#20](https://github.com/Igouist/Demo.DotnetCore.ConfigService/issues/20) 追蹤；完成前，程式與設計文件仍描述原本的嚴格行為。
