# UndoMod - Slay the Spire 2 전투 되돌리기 모드

전투 중 카드 사용이나 포션 사용을 **Ctrl+Z**로 되돌릴 수 있는 모드.

## 기능

- **Ctrl+Z**: 카드 사용 또는 포션 사용 직전 상태로 되돌리기
- 최대 20회까지 연속 되돌리기 가능
- 전투 시작 시 자동 초기화

### 복원되는 상태

| 항목 | 설명 |
|------|------|
| 플레이어 HP/블록 | 현재 HP, 최대 HP, 블록 |
| 에너지/스타 | 에너지, 최대 에너지, 스타 |
| 카드 더미 | 핸드, 뽑을 더미, 버린 더미, 소멸 더미 (UI 포함) |
| 카드 업그레이드 | 전투 중 업그레이드/다운그레이드 상태 |
| 버프/디버프 | 플레이어, 적, 펫의 모든 파워 (수치 및 추가/제거) |
| 적 상태 | HP, 블록, 버프 |
| 펫 상태 | HP, 블록, 버프 |
| 포션 | ModelDb에서 새 인스턴스 생성 후 AddPotionInternal로 정식 복원 |
| 유물 카운터 | 서브클래스 내부 int 필드 탐색 + DisplayAmountChanged 이벤트 발화 |
| 구체 (디펙트) | 구체 타입, 패시브/이보크 값 |
| 턴 번호 | RoundNumber |

## 아키텍처

```
Ctrl+Z 입력
  ↓
UndoInput (백그라운드 스레드, 50ms 폴링)
  ↓ volatile bool 플래그
OnProcessFrame (메인 스레드, Godot ProcessFrame 시그널)
  ↓
UndoManager.Undo()
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
├── ModEntry.cs          # 모드 진입점 (Harmony 초기화, 입력 시작)
├── UndoManager.cs       # 스냅샷 스택 관리 (최대 20개)
├── UndoButton.cs        # Ctrl+Z 입력 감지 (백그라운드 폴링 + ProcessFrame)
├── CombatPatches.cs     # Harmony 패치 (카드/포션 사용 감지)
├── StateSnapshot.cs     # 상태 캡처/복원 핵심 로직
├── UndoMod.csproj       # 프로젝트 설정 (.NET 9.0, x64)
├── mod_manifest.json    # 모드 메타데이터
├── deploy.py            # 빌드 + 배포 스크립트
└── build_pck.py         # Godot 4 PCK 파일 생성
```

## 각 파일 설명

### ModEntry.cs
- `[ModInitializer]` 어트리뷰트로 게임 모드 로더에서 호출
- Harmony 패치 적용 (`PatchAll`)
- `UndoInput.Start()` 호출

### UndoManager.cs
- `Stack<StateSnapshot>` 관리 (최대 20개)
- `SaveSnapshot()`: `CombatManager.Instance.DebugOnlyGetState()`에서 상태 캡처
- `Undo()`: 스택에서 pop하여 `Restore()` 호출
- `Clear()`: 전투 시작/종료 시 스택 초기화

### UndoButton.cs (UndoInput)
- 백그라운드 스레드에서 Godot `Input.IsKeyPressed()` 20Hz 폴링
- Rising edge 감지 (키 눌림 순간만)
- `volatile bool`로 메인 스레드에 요청 전달
- Godot `ProcessFrame` 시그널에서 메인 스레드 실행
- Godot C# 가상 메서드(`_Ready`/`_Process`)가 모드 DLL에서 바인딩되지 않아 이 방식 사용

### CombatPatches.cs
- **CombatPatches**: `CombatManager.SetUpCombat` postfix → 전투 시작 시 패치 등록
- **PlayCardPatcher**: `PlayCardAction.ExecuteAction` prefix → 카드 사용 전 스냅샷
- **UsePotionPatcher**: 리플렉션으로 어셈블리 내 포션 관련 GameAction 타입 검색 → prefix 패치

### StateSnapshot.cs
상태 캡처/복원의 핵심 파일 (~1500줄).

#### Capture (캡처)
- `CombatState`에서 플레이어, 적, 카드, 포션, 유물 등 전체 상태 읽기
- 카드는 `CardModel` 참조 + ID/업그레이드 상태 저장
- 포션은 `Id.Entry` 문자열 저장 (사용 후 인스턴스 변이 방지)
- 핸드 카드의 `NHandCardHolder` UI 노드 참조도 저장

#### Restore (복원)
1. **기본 속성**: HP, 블록, 에너지, 스타, 턴 번호 (리플렉션)
2. **포션**: `ModelDb.AllPotions`에서 canonical 찾기 → `ToMutable()` → `AddPotionInternal(potion, slot, false)`
3. **유물 카운터**: 서브클래스의 int 필드 중 DisplayAmount와 일치하는 것 찾아 직접 설정 + 이벤트 발화
4. **카드 더미**: `CardPile.Clear(false)` → `AddInternal()` 루프 → `ContentsChanged` 이벤트
5. **핸드**: `NPlayerHand`에서 `NCard` 씬 로드/인스턴스화 → `SubscribeToModel()` + `Reload()`
6. **카드 업그레이드**: `DowngradeInternal()` / `FinalizeUpgradeInternal()` 호출
7. **파워**: Amount 업데이트 + 불필요한 파워 `RemoveInternal()` → `PowerRemoved` 이벤트
8. **적/펫**: HP, 블록, 파워 복원
9. **UI**: 더미 버튼 카운트 갱신, `CancelAllCardPlay()` 호출

## 핵심 기술적 해결책

### 포션 복원
- **문제**: 사용된 `PotionModel` 인스턴스는 내부 상태가 변해 재사용 불가
- **해결**: `ModelDb.AllPotions`에서 canonical 포션을 ID로 검색 → `ToMutable()`로 새 인스턴스 생성 → `player.AddPotionInternal(potion, slotIndex, false)`로 정식 추가 (silent=false로 `PotionProcured` 이벤트 발화 → UI 자동 갱신)

### 유물 카운터 복원
- **문제**: `DisplayAmount`가 computed property (CanWrite=False)인 유물 존재 (예: HappyFlower)
- **해결**: 유물 서브클래스의 모든 int 필드를 탐색하여 현재 `DisplayAmount`와 값이 같은 필드를 찾아 직접 설정 + `DisplayAmountChanged` 이벤트 발화

### 핸드 카드 UI 동기화
- **문제**: `CardPile` 데이터와 `NCard` UI 노드가 분리되어 있음
- **해결**: 기존 `NCard`의 `PackedScene` 경로를 가져와 새 인스턴스 생성 → `CardModel` 설정 → `SubscribeToModel()` + `Reload()` 호출

### 입력 감지
- **문제**: 모드 DLL에서 Godot `_Process()` 가상 메서드 바인딩 불가
- **해결**: 백그라운드 스레드 폴링 + `ProcessFrame` 시그널로 메인 스레드 실행

## 빌드 및 배포

```bash
# 빌드 + 배포 (한 번에)
python deploy.py
```

`deploy.py`가 자동으로 수행하는 작업:
1. `dotnet build -c Release` 실행
2. `build_pck.py`로 PCK 파일 생성
3. DLL + PCK를 게임 mods 폴더로 복사

배포 경로: `C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\mods\UndoMod\`

## 의존성

| 라이브러리 | 용도 |
|-----------|------|
| 0Harmony.dll | 게임 메서드 런타임 패치 |
| GodotSharp.dll | Godot 4 엔진 C# API (씬 트리, 시그널, 입력) |
| sts2.dll | Slay the Spire 2 게임 어셈블리 |

- **프레임워크**: .NET 9.0 / C# 12
- **플랫폼**: x64 (Windows)
