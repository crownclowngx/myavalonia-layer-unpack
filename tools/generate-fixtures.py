"""用 Python 标准库生成跨实现格式样本。只覆盖此脚本拥有的 Generated.* 文件，不读取用户资料。"""
import bz2
import gzip
import io
import lzma
from pathlib import Path
import tarfile

fixtures = Path(__file__).resolve().parents[1] / "tests/LayerUnpackPlugin.Headless.Tests/Fixtures"
payload = "层解 V1 / UTF-8 / 递归验证\n".encode("utf-8")
buffer = io.BytesIO()
with tarfile.open(fileobj=buffer, mode="w", format=tarfile.PAX_FORMAT) as archive:
    for name, data in [("普通目录/中文.txt", payload), ("empty.txt", b"")]:
        entry = tarfile.TarInfo(name)
        entry.size = len(data)
        entry.mtime = 0
        archive.addfile(entry, io.BytesIO(data))
tar_bytes = buffer.getvalue()
(fixtures / "Generated.utf8.tar").write_bytes(tar_bytes)
for suffix, compress in [("gz", lambda b: gzip.compress(b, mtime=0)), ("bz2", bz2.compress), ("xz", lzma.compress)]:
    (fixtures / f"Generated.utf8.tar.{suffix}").write_bytes(compress(tar_bytes))
    (fixtures / f"Generated.single.txt.{suffix}").write_bytes(compress(payload))
