# A replaceable-library folder bundle with GUI and CLI sharing one runtime.
from pathlib import Path

root = Path(SPECPATH).parent
a = Analysis([str(root / "scripts" / "frozen_entry.py")],
             pathex=[str(root / "src")],
             datas=[(str(root / "assets" / "brand" / "symbol.svg"), "assets/brand"),
                    (str(root / "assets" / "icons"), "assets/icons")],
             hiddenimports=["PySide6.QtSvg"],
             excludes=["pytest", "tkinter", "IPython", "matplotlib", "scipy", "tensorboard"])
pyz = PYZ(a.pure)
common = dict(exclude_binaries=True, debug=False, strip=False, upx=False,
              icon=str(root / "assets" / "icons" / "app-icon.ico"))
gui = EXE(pyz, a.scripts, [], name="FrameDoubler", console=False, **common)
cli = EXE(pyz, a.scripts, [], name="FrameDoubler-cli", console=True, **common)
COLLECT(gui, cli, a.binaries, a.datas, strip=False, upx=False, name="FrameDoubler")
