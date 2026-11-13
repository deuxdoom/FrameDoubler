"""Shared entry for the windowed and console executables in the folder bundle."""

import json
import sys
import traceback
from pathlib import Path


def self_test(output):
    """Exercise the bundled imports, assets and Qt event loop without downloading a model."""
    report = {}
    try:
        import subprocess
        import numpy
        import torch
        from PySide6.QtCore import QTimer, qVersion
        from PySide6.QtWidgets import QApplication
        from framedoubler import __version__
        from framedoubler.ffmpeg import ffmpeg_path, process_options
        from framedoubler.gui import MainWindow, asset_path
        from framedoubler.models import models_dir

        app = QApplication([])
        window = MainWindow()
        window.show()
        app.processEvents()
        assert window.start_button.text()
        assert asset_path("icons", "app-icon.ico").is_file()
        assert asset_path("brand", "symbol.svg").is_file()
        if torch.cuda.is_available():
            assert (torch.ones(2, device="cuda") + 1).sum().item() == 4
        report.update(version=__version__, python=sys.version, numpy=numpy.__version__, torch=torch.__version__,
                      cuda=torch.cuda.is_available(), qt=qVersion(), models=str(models_dir()),
                      ffmpeg=subprocess.check_output([ffmpeg_path(), "-version"], text=True,
                                                     **process_options()).splitlines()[0])
        QTimer.singleShot(50, window.close)
        app.exec()
        report["ok"] = True
    except Exception:
        report.update(ok=False, error=traceback.format_exc())
    Path(output).write_text(json.dumps(report, indent=2, ensure_ascii=False), encoding="utf-8")
    return 0 if report["ok"] else 1


if __name__ == "__main__":
    if len(sys.argv) == 3 and sys.argv[1] == "--self-test":
        raise SystemExit(self_test(sys.argv[2]))
    if Path(sys.executable).stem.lower().endswith("-cli"):
        from framedoubler.cli import main
    else:
        from framedoubler.gui import main
    raise SystemExit(main())
