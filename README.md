# TWHomeDB

테일즈위버 도구들이 읽는 **공개 데이터 저장소**입니다. TWChatOverlay · TWEtaChecker 가 `raw.githubusercontent.com/TWHome-Git/TWHomeDB/main/` 에서 받습니다.

| 파일 | 내용 | 누가 고치나 |
|---|---|---|
| `eta_ranking.json` · `eta_meta.json` · `eta_history.json` · `ranking_index.json` | 에타 랭킹 (매일 08시대) | 테일즈DB 서버가 자동으로 올림 (`Auto-update` / `Index` 커밋) |
| `BossTimer.json` · `DropItem.Json` · `EquipmentData.json` · `manifest.json` · `Recapture supplies.png` | 보스 타이머·드롭 아이템 등 | 손으로 고침 (바꾸면 `manifest.json` 판 번호도 올림) |

에타 랭킹 수집기는 TWPage 저장소(`collector/`)로 옮겼고, 수집·보관·집계는 테일즈DB 서버가 합니다.
`ranking_index.json` 은 날짜 → 그날 기준 `eta_ranking.json` 커밋 SHA 입니다.
