"""
Godot 4.x PCK (pack version 2) 파일 생성 스크립트.
mod_manifest.json을 res://mod_manifest.json 경로로 PCK에 패킹합니다.
"""
import struct
import hashlib
import os

PCK_NAME = "UndoMod"
MANIFEST_PATH = os.path.join(os.path.dirname(__file__), "mod_manifest.json")
OUTPUT_PATH = os.path.join(os.path.dirname(__file__), f"{PCK_NAME}.pck")


def build_pck():
    with open(MANIFEST_PATH, "rb") as f:
        manifest_data = f.read()

    magic = b"GDPC"
    pack_version = 2
    ver_major = 4
    ver_minor = 0
    ver_patch = 0
    pack_flags = 0
    file_base = 0

    res_path = "res://mod_manifest.json"
    path_bytes = res_path.encode("utf-8")
    path_padded_len = len(path_bytes)
    path_pad = (4 - (path_padded_len % 4)) % 4
    path_padded_len += path_pad

    md5 = hashlib.md5(manifest_data).digest()

    header_size = 4 + 4 + 4 + 4 + 4 + 4 + 8 + 64 + 4
    entry_size = 4 + path_padded_len + 8 + 8 + 16 + 4
    file_data_offset = header_size + entry_size

    with open(OUTPUT_PATH, "wb") as f:
        f.write(magic)
        f.write(struct.pack("<I", pack_version))
        f.write(struct.pack("<I", ver_major))
        f.write(struct.pack("<I", ver_minor))
        f.write(struct.pack("<I", ver_patch))
        f.write(struct.pack("<I", pack_flags))
        f.write(struct.pack("<Q", file_base))
        f.write(b"\x00" * 64)
        f.write(struct.pack("<I", 1))

        f.write(struct.pack("<I", path_padded_len))
        f.write(path_bytes)
        f.write(b"\x00" * path_pad)
        f.write(struct.pack("<Q", file_data_offset))
        f.write(struct.pack("<Q", len(manifest_data)))
        f.write(md5)
        f.write(struct.pack("<I", 0))

        f.write(manifest_data)

    print(f"PCK 생성 완료: {OUTPUT_PATH} ({os.path.getsize(OUTPUT_PATH)} bytes)")


if __name__ == "__main__":
    build_pck()
