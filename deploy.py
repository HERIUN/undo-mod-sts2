"""
UndoMod DLL과 PCK를 게임 mods 폴더에 복사하는 배포 스크립트.
사용법: python deploy.py
"""
import shutil
import os
import subprocess
import sys

GAME_PATH = r"C:\Program Files (x86)\Steam\steamapps\common\Slay the Spire 2"
MOD_NAME = "UndoMod"
MOD_DIR = os.path.dirname(__file__)
DLL_PATH = os.path.join(MOD_DIR, "bin", "Release", "net9.0", f"{MOD_NAME}.dll")
MANIFEST_PATH = os.path.join(MOD_DIR, "mod_manifest.json")
DEST_DIR = os.path.join(GAME_PATH, "mods", MOD_NAME)

# C# 빌드
print("DLL 빌드 중...")
subprocess.run(["dotnet", "build", "-c", "Release"], cwd=MOD_DIR, check=True)

if not os.path.exists(DLL_PATH):
    print(f"오류: DLL을 찾을 수 없습니다: {DLL_PATH}")
    sys.exit(1)

os.makedirs(DEST_DIR, exist_ok=True)
shutil.copy2(DLL_PATH, DEST_DIR)
shutil.copy2(MANIFEST_PATH, DEST_DIR)

print(f"\n배포 완료:")
print(f"  DLL: {DLL_PATH} -> {DEST_DIR}")
print(f"  Manifest: {MANIFEST_PATH} -> {DEST_DIR}")
print(f"\n게임 mods 폴더: {DEST_DIR}")
print("게임을 재시작하면 모드가 로드됩니다.")
