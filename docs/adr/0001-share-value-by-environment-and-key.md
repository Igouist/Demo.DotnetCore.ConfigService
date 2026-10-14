---
status: accepted
date: 2026-08-27
---

# 每個 Environment 與 Configuration Key 只有一個共享中央值

在同一個 Environment 內，一個 Configuration Key 只代表一個共享的中央值，因此一個 Configuration Entry 由 Environment 與 Configuration Key 唯一識別。若不同 Consumer Application 需要不同值，必須使用不同的 Configuration Key，而不是在同一個 key 下加入 consumer-specific scope；這能讓一次中央修改一致地解決多個應用程式重複修改設定的問題。

## Considered Options

- 相同 Environment 與 Configuration Key 對所有 Consumer Application 共享同一中央值。
- 讓相同 Configuration Key 依 Consumer Application、ConfigSet 或其他 scope 回傳不同值。

## Consequences

- 相同 key 在相同 environment 下具有單一、可預測的語意。
- Configuration server 不需要 Consumer Application 身分才能決定值。
- 不同預期值必須透過不同的 Configuration Key 明確命名。
- 若未來需要針對單一 Consumer Application 灰度不同值，必須建立新 key 或另行設計 scope，不能在既有 key 下隱式覆寫。
