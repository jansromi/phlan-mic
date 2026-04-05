#!/usr/bin/env python3

from __future__ import annotations

import shutil
import struct
import subprocess
import tempfile
from pathlib import Path


def read_png_size(path: Path) -> tuple[int, int]:
    png_bytes = path.read_bytes()
    png_signature = b"\x89PNG\r\n\x1a\n"
    if not png_bytes.startswith(png_signature):
        raise SystemExit(f"error: {path} is not a PNG file")
    if len(png_bytes) < 24 or png_bytes[12:16] != b"IHDR":
        raise SystemExit(f"error: {path} does not contain a readable PNG header")

    return struct.unpack(">II", png_bytes[16:24])


def validate_square_png(path: Path, minimum_size: int = 1) -> tuple[int, int]:
    width, height = read_png_size(path)
    if width != height:
        raise SystemExit(f"error: {path} must be square, got {width}x{height}")
    if width < minimum_size:
        raise SystemExit(f"error: {path} must be at least {minimum_size}x{minimum_size}, got {width}x{height}")
    if width > 4096:
        raise SystemExit(f"error: {path} is unexpectedly large at {width}x{height}")

    return width, height


def resize_png(source: Path, destination: Path, size: int) -> bytes:
    subprocess.run(
        [
            "sips",
            "-s",
            "format",
            "png",
            "-z",
            str(size),
            str(size),
            str(source),
            "--out",
            str(destination),
        ],
        check=True,
        capture_output=True,
    )
    return destination.read_bytes()


def main() -> int:
    repo_root = Path(__file__).resolve().parent.parent
    windows_icons_dir = repo_root / "assets" / "app-icons" / "windows"
    large_source_icon = windows_icons_dir / "phlanmic-large.png"
    small_source_icon = windows_icons_dir / "phlanmic-small.png"
    output_icon = (
        repo_root
        / "apps"
        / "windows-host"
        / "src"
        / "PhlanMic.WindowsHost.Ui"
        / "Assets"
        / "AppIcon"
        / "app.ico"
    )

    if shutil.which("sips") is None:
        raise SystemExit("error: sips is required to generate the Windows app icon")

    if not large_source_icon.is_file():
        raise SystemExit(f"error: missing large source icon at {large_source_icon}")
    if not small_source_icon.is_file():
        raise SystemExit(f"error: missing small source icon at {small_source_icon}")

    validate_square_png(large_source_icon, minimum_size=256)
    validate_square_png(small_source_icon, minimum_size=64)

    output_icon.parent.mkdir(parents=True, exist_ok=True)
    icon_payloads: list[tuple[int, bytes]] = []

    with tempfile.TemporaryDirectory(prefix="phlanmic-win-icon-") as temp_dir_name:
        temp_dir = Path(temp_dir_name)

        # Windows ICO entries top out at 256x256. Use the large source for
        # higher-resolution slots and the small source when explicitly provided
        # for the compact slots.
        for size in (256, 128):
            payload = resize_png(large_source_icon, temp_dir / f"{size}.png", size)
            icon_payloads.append((size, payload))

        for size in (64, 48, 32, 16):
            payload = resize_png(small_source_icon, temp_dir / f"{size}.png", size)
            icon_payloads.append((size, payload))

    icon_header = struct.pack("<HHH", 0, 1, len(icon_payloads))
    directory_entries = bytearray()
    image_data = bytearray()
    image_offset = 6 + (16 * len(icon_payloads))

    for size, png_bytes in icon_payloads:
        color_count = 0
        reserved = 0
        planes = 1
        bit_count = 32
        image_size = len(png_bytes)
        directory_entries.extend(
            struct.pack(
                "<BBBBHHII",
                size if size < 256 else 0,
                size if size < 256 else 0,
                color_count,
                reserved,
                planes,
                bit_count,
                image_size,
                image_offset,
            )
        )
        image_data.extend(png_bytes)
        image_offset += image_size

    output_icon.write_bytes(icon_header + directory_entries + image_data)
    print(f"Generated Windows app icon at {output_icon}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
