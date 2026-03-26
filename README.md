# UndoMod - Slay the Spire 2 전투 되돌리기 모드

전투 중 카드 사용, 포션 사용, 턴 종료를 **Ctrl+Z**로 되돌릴 수 있는 모드.

---

## 설치 방법

### 일반 사용자 (빌드 없이 설치)

1. [릴리즈 페이지](https://github.com/HERIUN/undo-mod-sts2/releases)에서 `UndoMod.dll`, `UndoMod.pck`, `mod_manifest.json` 세 파일을 다운로드합니다.

2. 게임 폴더 안에 `mods\UndoMod\` 디렉토리를 만들고 세 파일을 넣습니다:

   ```
   C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2\
   └── mods\
       └── UndoMod\
           ├── UndoMod.dll
           ├── UndoMod.pck
           └── mod_manifest.json
   ```

3. 게임을 시작하면 모드가 자동으로 로드됩니다.

별도 프로그램 설치는 필요 없습니다. 위 세 파일만 올바른 경로에 넣으면 됩니다.

### 개발자 (소스에서 빌드)

소스 코드를 수정하거나 직접 빌드하려면 **.NET 9.0 SDK**가 필요합니다.

```bash
# 빌드 + 배포 (한 번에)
cd undo_mod
python deploy.py
```

또는 수동으로:

```bash
dotnet build -c Release
```

빌드된 DLL은 `bin\Release\net9.0\UndoMod.dll`에 생성됩니다.
이 파일과 `UndoMod.pck`, `mod_manifest.json`을 게임의 `mods\UndoMod\` 폴더에 복사하면 됩니다.

---

## 사용법

### 키보드

| 입력 | 동작 |
|------|------|
| **Ctrl+Z** | 마지막 액션 되돌리기 |

전투 중 언제든 `Ctrl+Z`를 누르면 직전 액션 이전 상태로 복원됩니다.
최대 **100회** 연속 되돌리기가 가능합니다.

### TCP 원격 제어

프로그래밍적으로 undo를 제어할 수 있는 TCP 서버가 포트 **38643**에서 동작합니다.

| 명령 | 응답 | 설명 |
|------|------|------|
| `undo\n` | `ok\n` / `fail\n` | 마지막 액션 되돌리기 |
| `save\n` | `ok\n` | 현재 상태 스냅샷 수동 저장 |
| `count\n` | `숫자\n` | 저장된 스냅샷 개수 조회 |
| `clear\n` | `ok\n` | 스냅샷 스택 초기화 |

Python 클라이언트 예시 (`undo_client.py`):

```python
from undo_client import undo, save_snapshot, snapshot_count

print(f"스냅샷 수: {snapshot_count()}")
print(f"Undo 결과: {undo()}")
```

### Undo가 차단되는 상황

다음 상황에서는 게임 안정성을 위해 undo가 자동으로 차단됩니다:

- 액션 실행 중 (카드 효과 처리, 애니메이션 진행 중)
- 카드 선택 UI가 열려 있는 경우 (불타는 조약, 탐색 등)
- 복원이 이미 진행 중인 경우

차단 시 Godot 콘솔에 사유가 로그로 출력됩니다.

---

## 스냅샷에 저장되는 정보

스냅샷은 카드 사용, 포션 사용/버리기, 턴 종료 **직전**에 자동으로 생성됩니다.

### 플레이어 상태

| 항목 | 세부 내용 |
|------|-----------|
| HP | 현재 HP, 최대 HP |
| 블록 | 현재 블록 수치 |
| 에너지 | 현재 에너지, 최대 에너지 |
| 스타 | 현재 스타 수치 |
| 골드 | RunState의 골드 |

### 카드 관련

| 항목 | 세부 내용 |
|------|-----------|
| 핸드 | 현재 손패의 모든 카드 (CardModel 참조 + UI 홀더) |
| 뽑을 더미 | DrawPile 내 모든 카드 |
| 버린 더미 | DiscardPile 내 모든 카드 |
| 소멸 더미 | ExhaustPile 내 모든 카드 |
| 업그레이드 상태 | 전투 중 업그레이드/다운그레이드 여부 |
| 에너지 비용 | 기본 비용(_base) + 비용 수정자(_localModifiers) |
| 구속 (Affliction) | 종류(Id), 카테고리, 수치 (예: Bound, Hexed) |
| 인챈트 (Enchantment) | 종류(Id), 카테고리, 수치(_amount), 상태(_status), 서브클래스 고유 필드 (기세 _extraDamage 등) |
| DynamicVars | 딥카피 — 유전 알고리즘 등 누적 수치 완전 복원 |
| 서브클래스 필드 | CardModel 전체 계층의 int/bool/decimal 필드 |

### 적 상태

| 항목 | 세부 내용 |
|------|-----------|
| HP/블록 | 각 적의 현재 HP, 최대 HP, 블록 |
| 생존 여부 | IsAlive — 죽은 적도 원래 위치에 부활 |
| 버프/디버프 | 모든 파워의 종류, 수치, _internalData (MemberwiseClone 딥카피) |
| 다음 행동 (Intent) | Monster.NextMove 상태 (SetMoveImmediate로 복원) |
| 소환된 적 | 스냅샷에 없는 소환 적은 자동 제거 |
| 도주 크리처 | _escapedCreatures 리스트 |
| 화면 위치 | NCreature 노드의 원래 Position 저장/복원 |

### 펫 상태

| 항목 | 세부 내용 |
|------|-----------|
| HP/블록 | 현재 HP, 최대 HP, 블록 |
| 버프/디버프 | 모든 파워 |

### 유물

| 항목 | 세부 내용 |
|------|-----------|
| 카운터 | DisplayAmount에 대응하는 내부 int 필드 (서브클래스별 자동 탐색) |
| 상태 (Status) | RelicStatus enum 값 |
| 추가 필드 | 서브클래스의 모든 bool/int 필드 (턴 중 변경되는 내부 상태) |
| DynamicVars | MemberwiseClone으로 deep copy (계산 변수 복원) |

### 포션

| 항목 | 세부 내용 |
|------|-----------|
| 슬롯 | 각 슬롯의 포션 참조 + MutableClone |
| 비주얼 | NPotionContainer/NPotionHolder를 통한 포션 UI 완전 재생성 |

### 구체 (디펙트)

| 항목 | 세부 내용 |
|------|-----------|
| 구체 목록 | 타입, 패시브 값, 이보크 값 |
| 용량 | OrbQueue.Capacity |
| UI | NOrbManager 재구성 (기존 NOrb 제거 → 새 슬롯 생성 → 모델 설정) |

### 전투 시스템 상태

| 항목 | 세부 내용 |
|------|-----------|
| 턴 번호 | RoundNumber |
| RNG 상태 | RunRng, PlayerRng, MonsterRng — seed + counter로 랜덤 결과 동기화 |
| 전투 기록 | History 엔트리 수 (스냅샷 이후 추가된 항목 제거) |
| 턴 플래그 | IsPlayPhase, EndingPlayerTurnPhaseOne/Two, IsEnemyTurnStarted, PlayerActionsDisabled 등 |
| ActionQueueSynchronizer | PlayPhase 전환 (enum 타입 동적 감지) + ActionExecutor Unpause |

---

## 스냅샷 저장 시점

Harmony prefix 패치로 액션 실행 **직전**에 자동 저장:

| 패치 대상 | 시점 |
|-----------|------|
| `PlayCardAction` 생성자 | 카드 사용 직전 |
| `EndPlayerTurnAction` 생성자 | 턴 종료 직전 |
| `UsePotionAction` 생성자 | 포션 사용 직전 |
| `DiscardPotionGameAction` 생성자 | 포션 버리기 직전 |

---

## 참고 모드(UndoAndRedo)와의 차이점

[UndoAndRedo](../reference_UndoAndRedo/) 모드를 참고하여 개발되었으며, 다음과 같은 차이가 있습니다.

### UndoMod에만 있는 기능

| 기능 | 설명 |
|------|------|
| **TCP 원격 제어** | 포트 38643 TCP 서버로 외부 프로그램에서 undo/save/count/clear 명령 가능. 자동화/AI 에이전트 연동에 활용 |
| **RunState 덱 복원 (크로스턴 undo)** | 턴을 넘긴 후 되돌리기 시 `Player.Deck.Cards`와 `HasBeenRemovedFromState` 플래그를 복원. 카드 훔치는 적(ThievingHopper) 패턴 전으로 되돌려도 턴 종료 시 크래시 방지 |
| **TaskHelper.RunSafely 패치** | async 메서드에서 발생하는 NullReferenceException/ObjectDisposedException을 래핑하여 게임 크래시 방지 |
| **NCard 다층 안전망** | ObjectDisposedException 방지를 위한 5계층 방어 (IsInstanceValid prefix, Finalizer 등) |
| **ReplayWriter 오류 무시** | undo로 인한 ModelId 매핑 실패 시 리플레이 저장만 스킵 |
| **스냅샷 최대 100개** | 참고 모드의 50개 대비 2배 |

### UndoAndRedo에만 있는 기능

| 기능 | 설명 |
|------|------|
| **Redo (다시 실행)** | 오른쪽 화살표키로 되돌린 액션을 다시 실행 가능 |
| **CanUndoRedo 가드 체크** | InTransition, PlayQueue 비어있는지, CurrentSide == Player 등 더 세밀한 가드 체크 |
| **카드 설명 2단계 갱신** | CallDeferred + 다음 프레임 대기로 카드 비용/설명 텍스트를 2회 갱신하여 VoidForm 등 파워 의존 비용 정확 반영 |
| **NCardPlayQueue 정리** | 복원 시 stale 카드 플레이 큐 항목의 tween 정리 및 NCard QueueFree |
| **키보드 입력 방식** | `NGame._Input` Harmony 패치로 왼쪽/오른쪽 화살표 사용. UndoMod는 ProcessFrame에서 Ctrl+Z 폴링 |
| **파일 로그** | `UndoAndRedo.log` 파일에 별도 로깅. UndoMod는 `GD.Print`로 Godot 콘솔에 출력 |

### 구현 방식 차이

| 항목 | UndoMod | UndoAndRedo |
|------|---------|-------------|
| 포션 패치 | 생성자를 AccessTools로 동적 탐색 후 패치 | `[HarmonyPatch]` 어트리뷰트로 선언적 패치 |
| 스냅샷 저장 구조 | `Stack<StateSnapshot>` | `List<CombatSnapshot>` (Undo/Redo 양방향) |
| 패치 등록 | 코드에서 수동 `Harmony.Patch()` 호출 | `harmony.PatchAll()` + 어트리뷰트 자동 등록 |
| 전투 초기화 감지 | `CombatManager.SetupCombatState` Prefix | `CombatManager.Reset` Postfix |

---

## 파일 구조

```
undo_mod/
├── ModEntry.cs          # 모드 진입점 (Harmony 초기화, TCP 서버 시작)
├── UndoManager.cs       # 스냅샷 스택 관리 (최대 100개) + IsRestoring 가드
├── UndoButton.cs        # Ctrl+Z 입력 감지 (백그라운드 폴링 + ProcessFrame)
├── UndoTcpServer.cs     # TCP 서버 (포트 38643, undo/save/count/clear)
├── CombatPatches.cs     # Harmony 패치 (카드/포션/턴종료 감지 + 안전장치)
├── StateSnapshot.cs     # 상태 캡처/복원 핵심 로직
├── VisualRefresh.cs     # 비주얼 갱신 (핸드/파워/포션/구체/HP바 등)
├── UndoMod.csproj       # 프로젝트 설정 (.NET 9.0, x64)
├── UndoMod.pck          # Godot PCK 파일 (mod_manifest.json 포함, 모드 로더 필수)
├── mod_manifest.json    # 모드 메타데이터
└── deploy.py            # 빌드 + 배포 스크립트
```

---

## 안전장치

### IsRestoring 플래그
복원 진행 중에는 모든 스냅샷 트리거가 비활성화됩니다. 복원 중 카드/포션 관련 패치가 발동되어도 재귀 스냅샷이 생성되지 않습니다.

### IsFailed 스냅샷 거부
캡처 도중 예외가 발생하면 해당 스냅샷에 `IsFailed` 플래그가 설정되며, 스택에 push되지 않습니다.

### NCard 안전망 (다층 방어)
ObjectDisposedException을 방지하는 5계층 안전망:
1. `NCard.OnAfflictionChanged` 등에 `IsInstanceValid` prefix
2. `CardModel.AfflictInternal` 등에 Finalizer
3. `CardCmd.Afflict` 등에 Finalizer
4. `CardPileCmd.Draw`에 Finalizer
5. `CombatManager.SetupPlayerTurn`에 Finalizer

### TaskHelper.RunSafely 래핑
async 메서드에서 발생하는 예외를 잡아 게임 크래시를 방지합니다.

### ReplayWriter 오류 무시
undo로 인한 ModelId 매핑 실패 시 리플레이 저장만 스킵하여 전투 종료 플로우가 크래시되지 않도록 합니다.

### NCard 이벤트 구독 정리
복원 시 dispose된 Godot 노드를 타겟으로 하는 이벤트 구독을 선제적으로 제거합니다.

---

## 의존성

| 라이브러리 | 용도 |
|-----------|------|
| 0Harmony.dll | 게임 메서드 런타임 패치 |
| GodotSharp.dll | Godot 4 엔진 C# API (씬 트리, 시그널, 입력) |
| sts2.dll | Slay the Spire 2 게임 어셈블리 |

- **프레임워크**: .NET 9.0 / C# 12
- **플랫폼**: x64 (Windows)

---

## 알려진 제한사항

- 카드 선택 UI(불타는 조약, 탐색 등) 중에는 undo 불가 — 선택 완료/취소 후 가능
- Redo 기능 미지원 (Undo만 가능)
- 멀티플레이어 미지원
- 전투 외 상태(맵, 상점 등)는 대상이 아님
- 게임 업데이트 시 내부 필드명 변경으로 인해 일부 기능이 동작하지 않을 수 있음
