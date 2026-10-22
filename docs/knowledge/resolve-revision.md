# Resolve 回應的 `revision`

Resolve 回應的 `revision` 是一個短字串，代表「這次請求的設定鍵，在 Central 上各自是第幾版」。它讓維護人員不必看設定值，就能判斷兩次啟動拿到的設定是否相同。決策見[決策紀錄](../decisions/poc-decision-log.md) POC-062，契約見[設計文件](../design/config-distribution-poc.md)第 4.6 節。

```json
{
  "values": { "Demo:Message": "central-value" },
  "missingKeys": [ "Demo:LocalFallback" ],
  "revision": "r1-8bbca0abc2ea8e798b908301"
}
```

## 計算方式

Central 在每次 Resolve 時當場計算，不儲存：

1. 在讀取值的同一個交易內，取得每個請求設定鍵目前最新的項目版本號。從未建立的設定鍵記為 `0`；已移除的設定鍵使用它的 Remove 版本號。
2. 把中央環境與每個 `[設定鍵, 版本號]` 依序寫成 JSON 陣列。中央環境與設定鍵先轉成不變文化特性的大寫，設定鍵依序數排序。上面的例子是 `["UAT",["DEMO:LOCALFALLBACK",0],["DEMO:MESSAGE",3]]`。使用 JSON 是為了讓含有特殊字元的設定鍵也不會互相混淆。
3. 對這段 UTF-8 文字做 SHA-256，取前 12 個位元組轉成小寫十六進位，加上演算法前綴 `r1-`。

因此：

- 同一組設定鍵與版本，不論 Client 用什麼大小寫或順序請求，都得到相同的 `revision`。
- 不同服務要的設定鍵不同，各自的 `revision` 也互不影響；一個服務沒用到的設定鍵被修改，不會改變它的 `revision`。
- `revision` 不包含設定值，也無法反推出版本號。

## 怎麼比較

`revision` 是不透明字串，只能比較「相同」或「不同」。不要比大小、不要解析內容，也不要拿不同服務的 `revision` 互相比較（它們請求的設定鍵不同，本來就不會一樣）。Central 之後可能更換演算法，屆時前綴會改變，例如 `r2-`。

Client 會接受最長 128 個可見 ASCII 字元的 `revision`；舊版 Central 不回傳此欄位時，Client 照常啟動，日誌中的 `Revision` 為 null。

## 在哪裡看得到

- **使用端**：Client 在啟動成功的 Information 日誌帶出 `Revision`；Demo 的 `loaded` 標記也有 `revision` 欄位。
- **Central**：每次 Resolve 寫一筆 Information 日誌，包含 `TraceIdentifier`、中央環境、`Revision`，以及每個設定鍵的版本號（`KeyVersions`，例如 `Demo:Message=3`），不包含設定值。

## 使用場景

1. **重啟前後比對**：同一個服務重啟前後各 Resolve 一次，兩次 `revision` 不同，就知道兩次拿到的設定不一樣。
2. **多台機器比對**：同一個服務跑多台，其中一台行為異常時，比對各台日誌的 `revision`，看它是不是拿到不同的設定。
3. **追查具體差異**：拿 `revision` 到 Central 日誌找出當時的 `KeyVersions`，再用歷史紀錄端點（`GET /api/v1/management/configurations/history`）查出各版本的值。
4. **未來（尚未實作）**：最後已知可用快取用它判斷快取是否過期；輪詢時 Client 帶上次的 `revision`，Central 重新計算，相同就回「沒變」。

## 注意：指紋不同不一定代表值變了

下列情況也會讓 `revision` 改變：

- 服務本身要的設定鍵變了，例如新版程式多要或少要一個設定鍵。
- 有人 Set 了一模一樣的值。每次 Set 都會新增一個版本（POC-023），版本號變了，`revision` 就跟著變。
- 有人 Remove 之後又 Set 回原值。

所以準確的意思是「這次拿到的設定或要的設定鍵與上次不同，值得查一下」。反過來，`revision` 相同就表示每個請求設定鍵的版本都沒變，拿到的值也一定相同。

`revision` 只表示這組設定鍵的狀態有沒有變，不保證多個設定鍵會同時切換；同時修改多個設定鍵時，使用端仍可能在中間啟動，拿到一部分新值（見 POC-005）。
