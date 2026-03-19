# UndoMod - Slay the Spire 2 전투 되돌리기 모드

전투 중 카드 사용이나 포션 사용을 **Ctrl+Z**로 되돌릴 수 있는 모드.

## 기능

- **Ctrl+Z**: 카드 사용 또는 포션 사용 직전 상태로 되돌리기
- **카드 선택 UI 중 undo 차단**: 불타는 조약 등 카드 선택 중에는 undo 불가 (파이프라인 깨짐 방지)
- **TCP 원격 제어**: 포트 38643으로 프로그래밍적 undo/save/count/clear 명령
- 최대 20회까지 연속 되돌리기 가능
- 전투 시작 시 자동 초기화

### 복원되는 상태

| 항목 | 설명 |
|------|------|
| 플레이어 HP/블록 | 현재 HP, 최대 HP, 블록 |
| 에너지/스타 | 에너지, 최대 에너지, 스타 |
| 카드 더미 | 핸드, 뽑을 더미, 버린 더미, 소멸 더미 (UI 포함) |
| 카드 업그레이드 | 전투 중 업그레이드/다운그레이드 상태 |
| 카드 구속/인챈트 | Affliction, Enchantment 상태 복원 |
| 버프/디버프 | 플레이어, 적, 펫의 모든 파워 (수치 및 추가/제거) |
| 적 상태 | HP, 블록, 버프 |
| 펫 상태 | HP, 블록 |
| 포션 | ModelDb에서 새 인스턴스 생성 후 AddPotionInternal로 정식 복원 |
| 유물 카운터 | 서브클래스 내부 int 필드 탐색 + DisplayAmountChanged 이벤트 발화 |
| 구체 (디펙트) | 구체 타입, 패시브/이보크 값 |
| 턴 번호 | RoundNumber |
| PlayerActionsDisabled | 포션 사용 중 설정된 플래그 리셋 |

## 아키텍처

```
Ctrl+Z 입력 (또는 TCP "undo" 명령)
  ↓
UndoInput (백그라운드 스레드, 50ms 폴링)
  ↓ volatile bool 플래그
OnProcessFrame (메인 스레드, Godot ProcessFrame 시그널)
  ↓
UndoManager.Undo()
  ├─ 카드 선택 UI(IsInCardSelection) 체크 → 열려있으면 차단
  ↓ Stack<StateSnapshot> pop
StateSnapshot.Restore()
  ↓ 리플렉션 + Godot 씬 트리
게임 상태 + UI 복원
```

### 스냅샷 저장 시점

Harmony prefix 패치로 액션 실행 **직전**에 스냅샷 저장:

- `PlayCardAction.ExecuteAction()` — 카드 사용 직전
- `UsePotionAction.ExecuteAction()` — 포션 사용 직전
- `DiscardPotionGameAction.ExecuteAction()` — 포션 버리기 직전

## 파일 구조

```
undo_mod/
├── ModEntry.cs          # 모드 진입점 (Harmony 초기화, TCP 서버 시작)
├── UndoManager.cs       # 스냅샷 스택 관리 (최대 20개) + 카드 선택 UI 차단
├── UndoButton.cs        # Ctrl+Z 입력 감지 (백그라운드 폴링 + ProcessFrame)
├── UndoTcpServer.cs     # TCP 서버 (포트 38643, undo/save/count/clear)
├── CombatPatches.cs     # Harmony 패치 (카드/포션 사용 감지)
├── StateSnapshot.cs     # 상태 캡처/복원 핵심 로직
├── UndoMod.csproj       # 프로젝트 설정 (.NET 9.0, x64)
├── mod_manifest.json    # 모드 메타데이터
├── deploy.py            # 빌드 + 배포 스크립트
└── build_pck.py         # Godot 4 PCK 파일 생성
```

## 핵심 기술적 해결책

### 핸드 카드 UI 동기화 (데이터/시각 분리 구조)

게임은 카드 데이터(`CardPile`)와 시각(`NPlayerHand` → `CardHolderContainer` → `NHandCardHolder` → `NCard`)을 독립적으로 관리한다. CardPile 이벤트만으로는 NPlayerHand UI가 갱신되지 않으므로 양쪽을 모두 조작해야 한다.

**복원 흐름:**
1. `CardPile.Clear(false)` → `AddInternal(card, i, false)` → `InvokeContentsChanged()` — 데이터 복원
2. `CardHolderContainer`에서 `holder.CardModel` 프로퍼티로 현재 UI 카드 맵 구축
3. 스냅샷에 없는 카드 홀더 → `QueueFree()` 제거
4. 누락된 카드 → 기존 `NCard`의 `SceneFilePath`에서 `PackedScene` 로드 → `Instantiate()` → `Model` 설정 → `SubscribeToModel()` + `Reload()` → `NPlayerHand.Add(ncard, index)`

### 카드 선택 UI 중 undo 차단

불타는 조약 등 카드 선택을 요구하는 카드 사용 시, 선택 UI가 열린 상태에서 undo하면 게임의 async action 파이프라인(SelectCards Task)이 깨진다. `NPlayerHand.IsInCardSelection` 프로퍼티를 체크하여 undo를 차단한다.

### 포션 복원
- **문제**: 사용된 `PotionModel` 인스턴스는 내부 상태가 변해 재사용 불가
- **해결**: `ModelDb.AllPotions`에서 canonical 포션을 ID로 검색 → `ToMutable()`로 새 인스턴스 생성 → `player.AddPotionInternal(potion, slotIndex, false)`로 정식 추가

### 유물 카운터 복원
- **문제**: `DisplayAmount`가 computed property (CanWrite=False)인 유물 존재 (예: HappyFlower)
- **해결**: 유물 서브클래스의 모든 int 필드를 탐색하여 현재 `DisplayAmount`와 값이 같은 필드를 찾아 직접 설정 + `DisplayAmountChanged` 이벤트 발화

### 입력 감지
- **문제**: 모드 DLL에서 Godot `_Process()` 가상 메서드 바인딩 불가
- **해결**: 백그라운드 스레드 폴링 + `ProcessFrame` 시그널로 메인 스레드 실행

## TCP 프로토콜

포트 `38643`으로 TCP 연결 후 명령 전송:

| 명령 | 응답 | 설명 |
|------|------|------|
| `undo\n` | `ok\n` / `fail\n` | 마지막 액션 되돌리기 |
| `save\n` | `ok\n` | 현재 상태 스냅샷 저장 |
| `count\n` | `숫자\n` | 저장된 스냅샷 개수 |
| `clear\n` | `ok\n` | 스냅샷 스택 초기화 |

Python 클라이언트: `undo_client.py` (프로젝트 루트)

## 빌드 및 배포

```bash
# 빌드 + 배포 (한 번에)
python deploy.py
```

`deploy.py`가 자동으로 수행하는 작업:
1. `dotnet build -c Release` 실행
2. DLL + manifest를 게임 mods 폴더로 복사

배포 경로: `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\UndoMod\`

## 의존성

| 라이브러리 | 용도 |
|-----------|------|
| 0Harmony.dll | 게임 메서드 런타임 패치 |
| GodotSharp.dll | Godot 4 엔진 C# API (씬 트리, 시그널, 입력) |
| sts2.dll | Slay the Spire 2 게임 어셈블리 |

- **프레임워크**: .NET 9.0 / C# 12
- **플랫폼**: x64 (Windows)

## 알려진 제한사항

- 카드 선택 UI(불타는 조약, 탐색 등) 중에는 undo 불가 — 선택 완료/취소 후 undo 가능
- 적 의도(intent)는 복원되지 않음 (서버 측 결정)
- 전투 외 상태(맵, 상점 등)는 대상이 아님
