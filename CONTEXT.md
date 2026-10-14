# 設定分發

本文定義管理與分發共享設定值所使用的領域詞彙。應用程式可讀取這些設定值，不需要向設定伺服器登錄。

## 領域詞彙

**中央環境（Environment）**：
具有名稱且不區分大小寫的中央部署分區。同一分區內，每個設定鍵只有一個共享中央值。使用端應用程式必須明確選擇中央環境；它可以與應用程式的主機環境不同。
_避免用語_：主機環境（Host Environment）、使用端（Consumer）、應用程式範圍（application scope）

**主機環境（Host Environment）**：
使用端應用程式的主機框架所選擇的執行環境，用於決定本機行為與環境專屬設定。主機環境與查詢中央設定時使用的中央環境相互獨立。
_避免用語_：中央環境（Central Environment）、設定環境（Configuration Environment）

**設定鍵（Configuration Key）**：
單一邏輯設定的標準名稱，不區分大小寫，由一個或多個非空白區段組成，並以 `:` 分隔。同一中央環境內，所有要求相同設定鍵的使用端應用程式都會收到同一個中央值；`CentralConfig` 是保留的根區段。若需要不同的中央值，必須使用不同的設定鍵。
_避免用語_：使用端專屬別名（Consumer-specific alias）、ConfigSet 成員（ConfigSet member）

**設定項目（Configuration Entry）**：
由一個中央環境與一個設定鍵識別、集中管理的設定值。
_避免用語_：設定資料列（Setting row）、設定項（config item）

**項目版本（Entry Version）**：
設定項目不可變的歷史狀態，由伺服器編號。每個設定項目的版本號從一開始遞增；最大版本決定中央值目前存在或已移除。
_避免用語_：原地更新（In-place update）、目前資料列（current row）

**使用端應用程式（Consumer Application）**：
選擇並讀取設定鍵的應用程式。設定伺服器不登錄該應用程式，也不保存其設定鍵選擇。
_避免用語_：已登錄用戶端（Registered client）、下游服務（downstream service）

**設定鍵選擇（Key Selection）**：
由使用端應用程式擁有的宣告，明確列出要接收的設定鍵，以及每個鍵的缺值行為。變更設定鍵選擇屬於使用端部署事項，不需要變更伺服器登錄。
_避免用語_：ConfigSet 訂閱（ConfigSet subscription）、使用端登錄清單（consumer registry）、伺服器指派（server assignment）

**缺少的設定鍵（Missing Key）**：
設定鍵選擇中，在所要求的中央環境內沒有目前中央值的設定鍵。它與值為空字串的設定項目不同。
_避免用語_：空值（Empty value）、選擇失敗（failed selection）

**缺值行為（Missing Behavior）**：
由使用端擁有的規則，決定所選設定鍵缺少中央值時的行為：中央必要（Central Required）或允許後備（Fallback Allowed）。
_避免用語_：伺服器政策（Server policy）、全域後備（global fallback）

**中央必要設定鍵（Central-Required Key）**：
所選設定鍵中，必須具有中央值，使用端應用程式才能成功啟動的設定鍵。
_避免用語_：應用程式必要設定（Application-required setting）、選用後備（optional fallback）

**允許後備設定鍵（Fallback-Allowed Key）**：
所選設定鍵中，缺少中央值時不會使套件啟動失敗的設定鍵。其他設定來源可決定其有效值；若沒有來源提供該鍵，它仍保持缺少狀態，由使用端應用程式決定是否有效。
_避免用語_：靜默後備（Silent fallback）、選用設定（optional setting）

**移除（Removal）**：
將設定項目從具有中央值轉為沒有目前中央值的項目版本，並保留歷史。從未存在或已移除的設定項目不能再次移除；已移除的設定鍵對使用端應用程式而言屬於缺少的設定鍵。
_避免用語_：實體刪除（Physical deletion）、空字串（empty string）、重複刪除標記（repeated tombstone）
