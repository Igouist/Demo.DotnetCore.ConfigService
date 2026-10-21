# 用外部 SQLite 工具查看 Central 資料庫

用 DB Browser for SQLite、`sqlite3` 命令列或 Python 的 `sqlite3` 直接開啟 Central 的資料庫時，部分查詢會失敗並顯示：

```text
no such collation sequence: ORDINAL_IGNORE_CASE
```

這不表示資料庫損壞。本文說明原因、建議的查詢方式，以及必須直接查詢時的做法。

## 為什麼會出現這個錯誤

`ConfigurationEntries` 的 `Environment` 與 `ConfigurationKey` 欄位宣告了自訂定序規則 `ORDINAL_IGNORE_CASE`（[POC 設計](../design/config-distribution-poc.md) §3.1、§6.2）。SQLite 的自訂定序規則不存放在資料庫檔案裡，而是由每個連線在執行時註冊；Central 在開啟每個連線時，以 `StringComparer.OrdinalIgnoreCase` 註冊這個規則。外部工具的連線沒有註冊它，因此只要陳述式需要比較或排序這兩個欄位，SQLite 在準備陳述式時就會失敗。

以 SQLite 3.45 對 Central 建立的資料庫實測：

| 陳述式 | 結果 |
| --- | --- |
| `SELECT COUNT(*) FROM ConfigurationEntries` | 成功 |
| `SELECT * FROM ConfigurationEntries` | 成功 |
| 只以 `Id` 連接 `ConfigurationEntryVersions` 的 `JOIN` | 成功 |
| `WHERE Environment = 'HttpDemo'` | 失敗 |
| `ORDER BY Environment` | 失敗 |
| `INSERT INTO ConfigurationEntries ...` | 失敗（唯一條件約束需要該定序規則） |

## 建議做法：透過管理介面查詢

查看資料時，優先使用管理介面的 `current` 與 `history`，例如 [HTTP 呼叫檔](../demo/config-distribution-demo.http) 的 03、13、14。它們使用與 Central 相同的識別規則，也會正確區分空字串、從未建立與已移除。

## 必須直接查詢時

只做唯讀查詢，並以唯讀模式開啟資料庫。Central 仍在執行時也可以讀取，不需要先停止它。

### 在比較處明確指定內建定序規則

在 `WHERE` 的比較加上 `COLLATE NOCASE`，SQLite 就改用內建規則，不再需要 `ORDINAL_IGNORE_CASE`。這個寫法不需要註冊任何東西，適用於 `sqlite3` 命令列與 DB Browser for SQLite：

```sh
sqlite3 -readonly -header central-config.db "
  SELECT e.Environment, e.ConfigurationKey, v.Version, v.Operation, v.Value, v.CreatedAtUtc
  FROM ConfigurationEntries AS e
  JOIN ConfigurationEntryVersions AS v ON v.ConfigurationEntryId = e.Id
  WHERE e.Environment = 'HttpDemo' COLLATE NOCASE
  ORDER BY e.ConfigurationKey COLLATE NOCASE, v.Version;"
```

`NOCASE` 只對 ASCII 字母不區分大小寫。環境名稱或設定鍵含非 ASCII 字元時，結果可能與 Central 的判定不同。

### 在工具中註冊同名的定序規則

能註冊自訂定序規則的工具（例如 Python）可以註冊同名規則，原本的查詢就能照常執行：

```python
import sqlite3

def ordinal_ignore_case(left: str, right: str) -> int:
    left, right = left.upper(), right.upper()
    return (left > right) - (left < right)

conn = sqlite3.connect("file:central-config.db?mode=ro", uri=True)
conn.create_collation("ORDINAL_IGNORE_CASE", ordinal_ignore_case)

rows = conn.execute(
    """
    SELECT e.ConfigurationKey, v.Version, v.Operation, v.Value
    FROM ConfigurationEntries AS e
    JOIN ConfigurationEntryVersions AS v ON v.ConfigurationEntryId = e.Id
    WHERE e.Environment = ? AND e.ConfigurationKey = ?
    ORDER BY v.Version
    """,
    ("HttpDemo", "Demo:CentralRequiredMessage"),
).fetchall()
```

Python 的 `str.upper()` 與 .NET 的 `StringComparer.OrdinalIgnoreCase` 對部分非 ASCII 字元的處理不同（例如 `ß`），所以這只是近似規則，只適合查詢。

## 不要用外部工具寫入

即使註冊了定序規則，也不要用外部工具 `INSERT`、`UPDATE` 或 `DELETE`。Central 的寫入流程負責只追加的版本號、Set／Remove 的狀態轉換與值的限制；直接寫入會繞過這些規則，近似的定序規則也可能讓唯一條件約束與 Central 的判定不一致。上面的範例以唯讀模式開啟資料庫，寫入會失敗並顯示 `attempt to write a readonly database`。

需要修改資料時，使用管理介面的 Set 與 Remove；需要重來時，停止 Central 並建立新的資料庫。
