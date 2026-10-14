---
status: accepted
date: 2026-08-27
---

# Key Selection 由 Consumer Application 擁有

Key Selection 屬於 Consumer Application，由 consumer 宣告自己要讀取哪些精確的 Configuration Key，以及每個 key 的 Missing Behavior。Configuration server 不保存 consumer registry，也不替 consumer 維護指派關係；新增應用程式或修改 Key Selection 是 consumer-side deployment concern，不需要同步修改 server registration。

## Considered Options

- Consumer Application 擁有 Key Selection，server 只處理請求中的精確 key list。
- Configuration server 維護 consumer registry、ConfigSet 或指派關係，並決定每個 consumer 可見哪些 key。

## Consequences

- Server 與 Consumer Application 的生命週期保持低耦合。
- Missing Behavior 能依每個 consumer 的啟動需求決定，而不是成為 server-side global policy。
- POC 不具備 server-side consumer dependency、影響分析或 dead-key cleanup 資訊；若未來需要，必須另外取得 consumer inventory，而不是默默改變 Key Selection 的所有權。
- 未來若重新引入 key grouping，應優先作為 consumer-owned Key Selection 上的便利抽象，而不是預設回到 server-owned registry。
