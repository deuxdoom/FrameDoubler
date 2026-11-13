"""Qt desktop interface. Video work is performed exclusively by pipeline.run."""

import argparse
import sys
from dataclasses import dataclass
from fractions import Fraction
from pathlib import Path

from PySide6.QtCore import Qt, QThread, QTimer, QUrl, Signal
from PySide6.QtGui import QColor, QDesktopServices, QFont, QIcon, QPainter, QPen
from PySide6.QtWidgets import (
    QApplication, QCheckBox, QComboBox, QDoubleSpinBox, QFileDialog, QFormLayout,
    QFrame, QHBoxLayout, QHeaderView, QLabel, QLayout, QLineEdit, QMainWindow, QMessageBox,
    QProgressBar, QPushButton, QScrollArea, QSizePolicy, QSpinBox, QTableWidget,
    QTableWidgetItem, QVBoxLayout, QWidget,
)

from framedoubler import __version__, pipeline
from framedoubler.settings import Settings, load_settings, save_settings


TEXT = {
    "tagline": ("프레임 사이를, 움직임으로.", "Make room for motion."),
    "subtitle": ("녹화 영상을 분석하고, 사이의 프레임을 채웁니다.", "Analyze your capture. Fill the frames in between."),
    "add": ("+ 영상 추가", "+ Add videos"), "input": ("입력 영상", "INPUT VIDEO"),
    "drop": ("영상을 여기에 끌어다 놓으세요", "Drop your videos here"),
    "drop_hint": ("또는 ‘영상 추가’로 파일을 선택하세요", "Or choose files with ‘Add videos’"),
    "analysis": ("프레임 분석", "Frame analysis"), "file_fps": ("파일 프레임레이트", "File frame rate"),
    "motion_fps": ("추정 움직임 프레임레이트", "Estimated motion rate"),
    "file_hint": ("파일에 저장된 프레임의 주기", "The frame rate stored in the file"),
    "motion_hint": ("중복을 구별한 실제 움직임의 주기", "Motion rate after identifying duplicates"),
    "timeline": ("원본 · 중복 · 생성 프레임", "Original · duplicate · generated frames"),
    "settings": ("출력 설정", "Output settings"), "fps": ("목표 프레임레이트", "Target frame rate"),
    "auto": ("자동 · 입력에 맞춤", "Automatic · follow input"), "preset": ("프리셋", "Preset"),
    "youtube": ("YouTube · H.264", "YouTube · H.264"), "default": ("기본 · HEVC 품질 19", "Default · HEVC quality 19"),
    "high_fps_hint": ("60fps 초과는 기본 HEVC 프리셋을 사용합니다. YouTube 프리셋은 60fps까지 지원합니다.",
                      "Rates above 60fps use the default HEVC preset. The YouTube preset supports up to 60fps."),
    "folder": ("출력 폴더", "Output folder"), "browse": ("찾아보기", "Browse"),
    "same_folder": ("원본과 같은 폴더", "Same folder as the input"),
    "advanced": ("고급 설정", "Advanced settings"), "codec": ("영상 코덱", "Video codec"),
    "preset_default": ("프리셋 기본값", "Preset default"), "rate_mode": ("품질 설정", "Rate control"),
    "quality": ("품질 · 낮을수록 선명", "Quality · lower is better"), "bitrate": ("목표 비트레이트", "Target bitrate"),
    "hud": ("고정 HUD 영역", "Static HUD regions"),
    "hud_hint": ("x,y,너비,높이; 여러 영역은 세미콜론으로 구분", "x,y,width,height; separate regions with semicolons"),
    "dedup": ("반복 프레임 자동 판별", "Identify duplicate frames automatically"),
    "verify": ("변환 후 결과 검증", "Verify the converted video"),
    "overwrite": ("기존 출력 파일 덮어쓰기", "Overwrite existing output files"),
    "queue": ("작업 대기열", "Queue"), "name": ("영상", "Video"), "state": ("상태", "Status"),
    "output": ("결과 파일", "Output file"), "remove": ("선택 항목 제외", "Remove selected"),
    "start": ("변환 시작", "Start conversion"), "cancel": ("취소", "Cancel"),
    "open": ("결과 열기", "Open result"), "open_folder": ("폴더 열기", "Open folder"),
    "details": ("자세히", "Details"), "idle": ("영상을 추가하면 자동으로 분석합니다.", "Add a video to analyze it automatically."),
    "pending": ("대기", "Queued"), "analyzing": ("분석 중", "Analyzing"), "ready": ("변환 준비 완료", "Ready to convert"),
    "converting": ("변환 중", "Converting"), "cancelled": ("취소됨", "Cancelled"),
    "cancelling": ("취소하는 중…", "Cancelling…"), "error": ("오류", "Error"),
    "complete": ("변환 완료", "Conversion complete"), "verified": ("검증 통과", "Verification passed"),
    "warning": ("변환 완료 · 검증 확인 필요", "Converted · review verification"),
    "unsupported": ("지원하지 않는 입력", "Unsupported input"),
    "hdr": ("HDR 영상은 아직 지원하지 않습니다. SDR 녹화 파일을 선택하세요.", "HDR is not supported yet. Choose an SDR recording."),
    "analyze": ("입력 영상 분석 중", "Analyzing the input video"), "model": ("보간 모델 준비 중", "Preparing the interpolation model"),
    "convert": ("프레임 보간 및 인코딩 중", "Interpolating and encoding frames"),
    "mux": ("영상 파일 마무리 중", "Finishing the video file"), "verify_stage": ("출력 결과 검증 중", "Verifying the output"),
    "done": ("작업 완료", "Finished"), "batch_done": ("대기열 처리를 마쳤습니다.", "Queue processing finished."),
    "queue_summary": ("완료 {complete} · 오류 {failed} · 취소 {cancelled}", "Completed {complete} · errors {failed} · cancelled {cancelled}"),
    "sampled": ("{frames}프레임 분석 · 고유 {unique} · 중복 {duplicates} · 정지 {static}",
                "{frames} frames sampled · {unique} new · {duplicates} duplicate · {static} static"),
    "plan_dup": ("반복된 프레임을 구별하고 새 프레임을 만듭니다.", "Identify repeated frames and create new frames between originals."),
    "plan_low": ("원본 프레임 사이에 새 프레임을 만듭니다.", "Create new frames between the original frames."),
    "plan_high": ("이미 높은 움직임 프레임레이트입니다. 원본 프레임을 유지합니다.", "Motion is already at the file rate. Original frames are preserved."),
    "progress": ("{out:,} / {total:,}프레임 · 처리 {speed:.1f}fps · 남은 시간 {eta}",
                 "{out:,} / {total:,} frames · processing {speed:.1f}fps · remaining {eta}"),
    "recommended": ("자동 출력: {fps}fps · 원본 해상도 유지", "Automatic output: {fps}fps · original resolution"),
    "bad_path": ("읽을 수 있는 영상 파일을 선택하세요: {path}", "Choose a readable video file: {path}"),
    "bad_hud": ("HUD 영역을 x,y,너비,높이 형식으로 입력하세요.", "Enter HUD regions as x,y,width,height."),
    "not_verified": ("변환 완료 · 결과 검증은 실행하지 않았습니다.", "Converted · output verification was not run."),
    "output_summary": ("출력 {fps}fps · {frames:,}프레임", "Output {fps}fps · {frames:,} frames"),
    "check_summary": ("잔여 중복 {duplicates:.2%} · 길이 차이 {length:+.1f}ms",
                      "Duplicates {duplicates:.2%} · length difference {length:+.1f}ms"),
    "no_motion": ("분석 구간에 움직임이 없어 움직임 fps를 추정할 수 없습니다.",
                  "There is not enough motion in the sampled section to estimate its rate."),
    "settings_error": ("설정을 저장하지 못했습니다.", "Could not save settings."),
}

STYLE = """
QMainWindow, QWidget#root { background: #14181B; color: #F3F1EB; }
QWidget#analysisPanel { background: #14181B; }
QWidget { color: #F3F1EB; font-family: 'Malgun Gothic', 'Segoe UI'; font-size: 14px; }
QLabel { background: transparent; }
QLabel[role='muted'] { color: #A6B1B8; font-size: 12px; }
QLabel[role='title'] { font-size: 26px; font-weight: 700; }
QLabel[role='section'] { font-size: 16px; font-weight: 600; }
QLabel[role='metric'] { font-size: 32px; font-weight: 600; }
QLabel#motionMetric { color: #FF6B3D; }
QFrame[card='true'] { background: #1D2327; border: 1px solid #46535B; border-radius: 12px; }
QFrame#dropCard { border: 1px dashed #74818A; }
QFrame#dropCard[dragging='true'] { border: 2px solid #FF6B3D; }
QPushButton, QComboBox, QLineEdit, QSpinBox, QDoubleSpinBox {
 background: #111619; border: 1px solid #74818A; border-radius: 8px; min-height: 42px; padding: 0 12px;
}
QPushButton { background: #252D32; font-weight: 600; }
QPushButton:hover { background: #344049; border-color: #A6B1B8; }
QPushButton:focus, QComboBox:focus, QLineEdit:focus, QSpinBox:focus, QDoubleSpinBox:focus {
 border: 2px solid #F4BD83;
}
QPushButton#startButton { background: #FF6B3D; color: #14181B; border: 0; min-height: 48px; }
QPushButton#startButton:hover { background: #FF8863; }
QPushButton:disabled, QComboBox:disabled, QLineEdit:disabled { color: #7C8991; border-color: #46535B; background: #1D2327; }
QPushButton#startButton:disabled { background: #374249; color: #A6B1B8; }
QComboBox QAbstractItemView { background: #252D32; color: #F3F1EB; selection-background-color: #46535B; }
QComboBox::drop-down { border: 0; width: 24px; }
QScrollArea { border: 0; background: transparent; }
QScrollBar:vertical { background: #1D2327; width: 12px; margin: 0; }
QScrollBar::handle:vertical { background: #46535B; min-height: 28px; border-radius: 5px; }
QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical { height: 0; }
QScrollBar::add-page:vertical, QScrollBar::sub-page:vertical { background: transparent; }
QCheckBox { spacing: 8px; min-height: 40px; padding: 2px 0; }
QCheckBox::indicator { width: 17px; height: 17px; border: 1px solid #74818A; border-radius: 4px; background: #111619; }
QCheckBox::indicator:checked { background: #FF6B3D; border-color: #FF6B3D; image: url('@CHECKMARK@'); }
QTableWidget { background: #1D2327; alternate-background-color: #252D32; border: 1px solid #46535B; border-radius: 8px; }
QTableWidget::item { padding: 6px; }
QTableWidget::item:selected { background: #374249; color: #F3F1EB; }
QHeaderView::section { background: #252D32; color: #A6B1B8; border: 0; padding: 8px; font-size: 12px; }
QProgressBar { background: #252D32; border: 0; border-radius: 4px; min-height: 6px; max-height: 6px; }
QProgressBar::chunk { background: #FF6B3D; border-radius: 4px; }
QToolTip { color: #F3F1EB; background: #252D32; border: 1px solid #74818A; padding: 6px; }
"""


def asset_path(*parts) -> Path:
    root = Path(getattr(sys, "_MEIPASS", Path(__file__).resolve().parents[2]))
    return root / "assets" / Path(*parts)


def rate_text(rate) -> str:
    return f"{float(rate):.2f}".rstrip("0").rstrip(".")


@dataclass
class QueueEntry:
    path: Path
    state: str = "pending"
    analysis: pipeline.Analysis | None = None
    output: Path | None = None
    result: pipeline.Result | None = None
    message: str = ""


class Job(QThread):
    progress = Signal(object)
    succeeded = Signal(object)
    failed = Signal(str, bool)

    def __init__(self, entry, mode, destination, options, parent):
        super().__init__(parent)
        self.entry, self.mode = entry, mode
        self.destination, self.options = destination, options
        self.token = pipeline.CancellationToken()

    def run(self):
        try:
            result = pipeline.run(self.entry.path, self.destination, self.options,
                                  cancel=self.token, on_progress=self.progress.emit,
                                  analyze_only=self.mode == "analyze")
            self.succeeded.emit(result)
        except pipeline.Cancelled as exc:
            self.failed.emit(str(exc), True)
        except Exception as exc:
            self.failed.emit(str(exc), False)


class FrameStrip(QWidget):
    def __init__(self):
        super().__init__()
        self.kind = None
        self.setMinimumHeight(64)

    def paintEvent(self, event):
        if not self.kind:
            return
        before = ["A", "A", "B", "B"] if self.kind == "duplicated" else ["A", "B"]
        after = ["A", "+", "B"] if self.kind != "native-high" else ["A", "B"]
        tokens = before + ["→"] + after
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        width = min(64, (self.width() - 8 * (len(tokens) - 1)) // len(tokens))
        font = QFont(self.font())
        font.setPixelSize(16)
        font.setBold(True)
        painter.setFont(font)
        for i, token in enumerate(tokens):
            x = i * (width + 8)
            pen = QPen(QColor("#74818A"))
            if self.kind == "duplicated" and i in (1, 3):
                pen.setStyle(Qt.PenStyle.DashLine)
            painter.setPen(pen)
            if token != "→":
                painter.setBrush(QColor("#FF6B3D" if token == "+" else "#252D32"))
                painter.drawRoundedRect(x, 8, width, 42, 6, 6)
            painter.setPen(QColor("#14181B" if token == "+" else "#F3F1EB"))
            painter.drawText(x, 8, width, 42, Qt.AlignmentFlag.AlignCenter, token)


class ElidedLabel(QLabel):
    def __init__(self):
        super().__init__()
        self._full_text = ""
        self.setSizePolicy(QSizePolicy.Policy.Ignored, QSizePolicy.Policy.Preferred)
        self.setMinimumWidth(0)

    def setText(self, text):
        self._full_text = text
        self._elide()

    def _elide(self):
        super().setText(self.fontMetrics().elidedText(self._full_text, Qt.TextElideMode.ElideMiddle,
                                                     max(1, self.contentsRect().width())))

    def resizeEvent(self, event):
        self._elide()
        super().resizeEvent(event)


class MainWindow(QMainWindow):
    def __init__(self, language=None, output_dir=None):
        super().__init__()
        preferences = load_settings()
        self.lang = language or preferences.language
        initial_output = output_dir if output_dir is not None else preferences.output_dir
        self.entries: list[QueueEntry] = []
        self._worker = None
        self._running = self._paused = self._closing = False
        self._bindings = []
        self._status = "idle"
        self._message = ""
        self._job_options = pipeline.Options()
        self.setWindowTitle("FrameDoubler")
        self.setWindowIcon(QIcon(str(asset_path("icons", "app-icon.ico"))))
        available = QApplication.primaryScreen().availableGeometry()
        self.setMinimumSize(min(1000, available.width()), min(760, available.height()))
        self.resize(min(1280, available.width()), min(900, available.height()))
        self.setAcceptDrops(True)
        self.setStyleSheet(STYLE.replace("@CHECKMARK@", asset_path("icons", "checkmark.svg").as_posix()))
        root = QWidget(objectName="root")
        self.setCentralWidget(root)
        layout = QVBoxLayout(root)
        layout.setContentsMargins(28, 24, 28, 24)
        layout.setSpacing(16)

        header = QHBoxLayout()
        logo = QLabel()
        logo.setPixmap(QIcon(str(asset_path("brand", "symbol.svg"))).pixmap(48, 48))
        header.addWidget(logo)
        heading = QVBoxLayout()
        heading.addWidget(self.label("tagline", "title"))
        heading.addWidget(self.label("subtitle", "muted"))
        header.addLayout(heading, 1)
        self.language = QComboBox(objectName="languageCombo")
        self.language.addItems(["한국어", "English"])
        self.language.setCurrentIndex(0 if self.lang == "ko" else 1)
        self.language.setMinimumWidth(116)
        self.language.currentIndexChanged.connect(self.change_language)
        header.addWidget(self.language)
        self.add_button = self.button("add", self.choose_files, "addButton")
        self.add_button.setShortcut("Ctrl+O")
        header.addWidget(self.add_button)
        layout.addLayout(header)

        content = QHBoxLayout()
        content.setSpacing(24)
        analysis_panel = QWidget(objectName="analysisPanel")
        left = QVBoxLayout(analysis_panel)
        left.setContentsMargins(0, 0, 4, 0)
        left.setSizeConstraint(QLayout.SizeConstraint.SetMinimumSize)
        left.setSpacing(10)
        self.drop_card = QFrame(objectName="dropCard")
        self.drop_card.setProperty("card", True)
        card = QVBoxLayout(self.drop_card)
        card.setContentsMargins(20, 18, 20, 18)
        card.addWidget(self.label("input", "muted"))
        self.filename = self.bind(ElidedLabel(), "drop")
        self.filename.setProperty("role", "section")
        self.filename.setTextFormat(Qt.TextFormat.PlainText)
        self.filename.setMinimumHeight(26)
        self.filename.setMaximumHeight(32)
        self.metadata = self.label("drop_hint", "muted")
        self.metadata.setWordWrap(True)
        card.addWidget(self.filename)
        card.addWidget(self.metadata)
        left.addWidget(self.drop_card)
        left.addWidget(self.label("analysis", "section"))
        metrics = QHBoxLayout()
        for key, hint, attr in (("file_fps", "file_hint", "file_metric"), ("motion_fps", "motion_hint", "motion_metric")):
            panel = QFrame()
            panel.setProperty("card", True)
            box = QVBoxLayout(panel)
            box.setContentsMargins(16, 12, 16, 12)
            box.addWidget(self.label(key, "muted"))
            number = QLabel("—")
            number.setProperty("role", "metric")
            number.setMinimumHeight(48)
            if attr == "motion_metric":
                number.setObjectName("motionMetric")
            setattr(self, attr, number)
            metric_row = QHBoxLayout()
            metric_row.setSpacing(6)
            metric_row.addWidget(number)
            unit = QLabel("fps")
            unit.setProperty("role", "muted")
            metric_row.addWidget(unit, 0, Qt.AlignmentFlag.AlignVCenter)
            metric_row.addStretch()
            box.addLayout(metric_row)
            box.addWidget(self.label(hint, "muted"))
            metrics.addWidget(panel, 1)
        left.addLayout(metrics)
        self.plan = QLabel()
        self.plan.setTextFormat(Qt.TextFormat.PlainText)
        self.plan.setWordWrap(True)
        self.sampled = QLabel()
        self.sampled.setProperty("role", "muted")
        self.sampled.setWordWrap(True)
        left.addWidget(self.plan)
        left.addWidget(self.sampled)
        self.strip = FrameStrip()
        left.addWidget(self.strip)
        left.addWidget(self.label("timeline", "muted"))
        left.addStretch()
        analysis_scroll = QScrollArea()
        analysis_scroll.setWidgetResizable(True)
        analysis_scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        analysis_scroll.viewport().setAutoFillBackground(False)
        analysis_scroll.setWidget(analysis_panel)
        content.addWidget(analysis_scroll, 3)

        settings = QFrame()
        settings.setProperty("card", True)
        right = QVBoxLayout(settings)
        right.setContentsMargins(20, 16, 20, 16)
        right.setSpacing(10)
        right.addWidget(self.label("settings", "section"))
        right.addWidget(self.label("fps", "muted"))
        self.fps_combo = QComboBox(objectName="fpsCombo")
        self.fps_combo.addItem("", None)
        self.fps_combo.addItem("59.94 fps", Fraction(60000, 1001))
        self.fps_combo.addItem("60 fps", Fraction(60))
        self.fps_combo.addItem("119.88 fps", Fraction(120000, 1001))
        self.fps_combo.addItem("120 fps", Fraction(120))
        right.addWidget(self.fps_combo)
        self.recommended = QLabel()
        self.recommended.setProperty("role", "muted")
        right.addWidget(self.recommended)
        right.addWidget(self.label("preset", "muted"))
        self.preset_combo = QComboBox(objectName="presetCombo")
        self.preset_combo.addItem("", "youtube")
        self.preset_combo.addItem("", None)
        self.preset_combo.setCurrentIndex(0 if preferences.preset == "youtube" else 1)
        right.addWidget(self.preset_combo)
        self.high_fps_hint = self.label("high_fps_hint", "muted")
        self.high_fps_hint.setWordWrap(True)
        self.high_fps_hint.hide()
        right.addWidget(self.high_fps_hint)
        right.addWidget(self.label("folder", "muted"))
        folder_row = QHBoxLayout()
        self.output_dir = QLineEdit(objectName="outputFolder")
        self.output_dir.textChanged.connect(self.output_dir.setToolTip)
        development_output = Path(r"F:\Temp\FrameDoubler\out")
        self.output_dir.setText(initial_output if initial_output is not None else
                                str(development_output) if development_output.parent.exists() else "")
        folder_row.addWidget(self.output_dir, 1)
        self.browse_button = self.button("browse", self.choose_folder)
        folder_row.addWidget(self.browse_button)
        right.addLayout(folder_row)
        self.advanced_button = self.button("advanced", self.toggle_advanced)
        right.addWidget(self.advanced_button)
        self.advanced = QWidget()
        advanced = QFormLayout(self.advanced)
        advanced.setContentsMargins(0, 4, 0, 4)
        self.codec_combo = QComboBox()
        for text, value in (("", None), ("H.264", "h264_nvenc"), ("HEVC", "hevc_nvenc"), ("AV1", "av1_nvenc")):
            self.codec_combo.addItem(text, value)
        advanced.addRow(self.label("codec", "muted"), self.codec_combo)
        self.rate_mode = QComboBox()
        for value in (None, "quality", "bitrate"):
            self.rate_mode.addItem("", value)
        advanced.addRow(self.label("rate_mode", "muted"), self.rate_mode)
        self.quality = QSpinBox()
        self.quality.setRange(1, 51)
        self.quality.setValue(19)
        advanced.addRow(self.label("quality", "muted"), self.quality)
        self.bitrate = QDoubleSpinBox()
        self.bitrate.setRange(0.1, 400)
        self.bitrate.setValue(24)
        self.bitrate.setSuffix(" Mbps")
        advanced.addRow(self.label("bitrate", "muted"), self.bitrate)
        self.hud = QLineEdit()
        advanced.addRow(self.label("hud", "muted"), self.hud)
        self.dedup = QCheckBox()
        self.bind(self.dedup, "dedup")
        self.dedup.setChecked(True)
        advanced.addRow(self.dedup)
        self.advanced.hide()
        right.addWidget(self.advanced)
        self.verify_box = QCheckBox(objectName="verifyCheckBox")
        self.bind(self.verify_box, "verify")
        self.verify_box.setChecked(True)
        self.overwrite_box = QCheckBox(objectName="overwriteCheckBox")
        self.bind(self.overwrite_box, "overwrite")
        right.addWidget(self.verify_box)
        right.addWidget(self.overwrite_box)
        right.addStretch()
        scroll = QScrollArea()
        scroll.setWidgetResizable(True)
        scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        scroll.setWidget(settings)
        scroll.setMinimumWidth(350)
        scroll.setMaximumWidth(450)
        content.addWidget(scroll, 2)
        layout.addLayout(content, 1)
        self._setting_widgets = [self.fps_combo, self.preset_combo, self.output_dir, self.browse_button,
                                 self.codec_combo, self.rate_mode, self.quality, self.bitrate, self.hud,
                                 self.dedup, self.verify_box, self.overwrite_box]

        queue_heading = QHBoxLayout()
        queue_heading.addWidget(self.label("queue", "section"))
        queue_heading.addStretch()
        self.remove_button = self.button("remove", self.remove_selected)
        queue_heading.addWidget(self.remove_button)
        layout.addLayout(queue_heading)
        self.table = QTableWidget(0, 3, objectName="queueTable")
        self.table.setShowGrid(False)
        self.table.setAlternatingRowColors(True)
        self.table.setSelectionBehavior(QTableWidget.SelectionBehavior.SelectRows)
        self.table.setSelectionMode(QTableWidget.SelectionMode.SingleSelection)
        self.table.setEditTriggers(QTableWidget.EditTrigger.NoEditTriggers)
        self.table.verticalHeader().hide()
        self.table.verticalHeader().setDefaultSectionSize(38)
        header = self.table.horizontalHeader()
        header.setSectionResizeMode(0, QHeaderView.ResizeMode.Stretch)
        header.setSectionResizeMode(1, QHeaderView.ResizeMode.ResizeToContents)
        header.setSectionResizeMode(2, QHeaderView.ResizeMode.Stretch)
        self.table.setMinimumHeight(100)
        self.table.setMaximumHeight(140)
        self.table.itemSelectionChanged.connect(self.show_selected)
        layout.addWidget(self.table)

        self.progress_bar = QProgressBar()
        self.progress_bar.setTextVisible(False)
        layout.addWidget(self.progress_bar)
        footer = QHBoxLayout()
        status_box = QVBoxLayout()
        self.status_label = QLabel()
        self.status_label.setWordWrap(True)
        self.status_label.setMaximumHeight(44)
        self.status_label.setTextFormat(Qt.TextFormat.PlainText)
        self.progress_label = QLabel()
        self.progress_label.setProperty("role", "muted")
        self.progress_label.setWordWrap(True)
        self.progress_label.setMaximumHeight(40)
        status_box.addWidget(self.status_label)
        status_box.addWidget(self.progress_label)
        footer.addLayout(status_box, 1)
        self.details_button = self.button("details", self.show_details)
        self.open_button = self.button("open", self.open_result)
        self.folder_button = self.button("open_folder", self.open_folder)
        self.cancel_button = self.button("cancel", self.cancel_job, "cancelButton")
        self.cancel_button.setShortcut("Esc")
        self.start_button = self.button("start", self.start_queue, "startButton")
        self.start_button.setShortcut("Ctrl+Return")
        self.start_button.setMinimumWidth(190)
        for button in (self.details_button, self.open_button, self.folder_button, self.cancel_button, self.start_button):
            footer.addWidget(button)
        layout.addLayout(footer)
        self.retranslate()
        self.update_controls()
        self._save_timer = QTimer(self)
        self._save_timer.setSingleShot(True)
        self._save_timer.setInterval(300)
        self._save_timer.timeout.connect(self.save_preferences)
        self.language.currentIndexChanged.connect(self.schedule_save)
        self.preset_combo.currentIndexChanged.connect(self.schedule_save)
        self.output_dir.textChanged.connect(self.schedule_save)
        self.fps_combo.currentIndexChanged.connect(self.update_target_fps)

    def update_target_fps(self, *args):
        fps = self.fps_combo.currentData()
        high = fps is not None and fps > 60
        self.preset_combo.model().item(0).setEnabled(not high)
        if high and self.preset_combo.currentData() == "youtube":
            self.preset_combo.setCurrentIndex(1)
        self.high_fps_hint.setVisible(high)

    def schedule_save(self, *args):
        self._save_timer.start()

    def save_preferences(self):
        folder = self.output_dir.text().strip()
        if folder and not Path(folder).is_absolute():
            folder = str(Path(folder).resolve())
        preferences = Settings(preset="youtube" if self.preset_combo.currentData() == "youtube" else "default",
                               output_dir=folder, language=self.lang)
        try:
            save_settings(preferences)
        except OSError as exc:
            self.set_status("settings_error", str(exc))

    def t(self, key, **values):
        return TEXT[key][0 if self.lang == "ko" else 1].format(**values)

    def bind(self, widget, key):
        self._bindings.append((widget, key))
        widget.setText(self.t(key))
        return widget

    def label(self, key, role=None):
        widget = self.bind(QLabel(), key)
        widget.setSizePolicy(QSizePolicy.Policy.Preferred, QSizePolicy.Policy.Fixed)
        if role:
            widget.setProperty("role", role)
        if role == "muted":
            widget.setWordWrap(True)
        return widget

    def button(self, key, slot, name=""):
        widget = self.bind(QPushButton(objectName=name), key)
        widget.clicked.connect(slot)
        return widget

    def change_language(self, index):
        self.lang = "ko" if index == 0 else "en"
        self.retranslate()

    def retranslate(self):
        for widget, key in self._bindings:
            widget.setText(self.t(key))
        self.fps_combo.setItemText(0, self.t("auto"))
        self.preset_combo.setItemText(0, self.t("youtube"))
        self.preset_combo.setItemText(1, self.t("default"))
        self.codec_combo.setItemText(0, self.t("preset_default"))
        for i, key in enumerate(("preset_default", "quality", "bitrate")):
            self.rate_mode.setItemText(i, self.t(key))
        self.output_dir.setPlaceholderText(self.t("same_folder"))
        self.hud.setPlaceholderText("x,y,w,h; x,y,w,h")
        self.hud.setToolTip(self.t("hud_hint"))
        self.table.setHorizontalHeaderLabels([self.t(key) for key in ("name", "state", "output")])
        for entry in self.entries:
            self.update_row(entry)
        self.show_selected()
        self.render_status()

    def toggle_advanced(self):
        self.advanced.setVisible(not self.advanced.isVisible())

    def choose_files(self):
        paths, _ = QFileDialog.getOpenFileNames(self, self.t("add"), "", "Video (*.mp4 *.mkv *.mov *.avi *.m4v *.webm *.ts *.m2ts);;All files (*)")
        self.add_paths(paths)

    def choose_folder(self):
        path = QFileDialog.getExistingDirectory(self, self.t("folder"), self.output_dir.text())
        if path:
            self.output_dir.setText(path)

    def dragEnterEvent(self, event):
        if event.mimeData().hasUrls() and all(url.isLocalFile() for url in event.mimeData().urls()):
            event.acceptProposedAction()
            self.drop_card.setProperty("dragging", True)
            self.drop_card.style().unpolish(self.drop_card)
            self.drop_card.style().polish(self.drop_card)

    def dragLeaveEvent(self, event):
        self._clear_drag()

    def _clear_drag(self):
        self.drop_card.setProperty("dragging", False)
        self.drop_card.style().unpolish(self.drop_card)
        self.drop_card.style().polish(self.drop_card)

    def dropEvent(self, event):
        self._clear_drag()
        self.add_paths([url.toLocalFile() for url in event.mimeData().urls() if url.isLocalFile()])
        event.acceptProposedAction()

    def add_paths(self, paths):
        known = {entry.path for entry in self.entries}
        for text in paths:
            path = Path(text).resolve()
            if path in known:
                continue
            if not path.is_file():
                self.set_status("error", self.t("bad_path", path=path))
                continue
            entry = QueueEntry(path)
            self.entries.append(entry)
            known.add(path)
            self.table.insertRow(self.table.rowCount())
            self.update_row(entry)
        if self.entries and self.table.currentRow() < 0:
            self.table.setCurrentCell(0, 0)
        self._paused = False
        self.update_controls()
        self.pump()

    def selected(self):
        row = self.table.currentRow()
        return self.entries[row] if 0 <= row < len(self.entries) else None

    def update_row(self, entry):
        row = self.entries.index(entry)
        values = (entry.path.name, self.t(entry.state), str(entry.output or ""))
        for column, text in enumerate(values):
            item = QTableWidgetItem(text)
            item.setToolTip(str(entry.path) if column == 0 else text if column == 2 else entry.message or text)
            if column == 1:
                colour = "#8DD9AB" if entry.state in ("ready", "verified", "complete") else "#FF8E89" if entry.state in ("error", "unsupported") else "#A6B1B8"
                item.setForeground(QColor(colour))
            self.table.setItem(row, column, item)

    def show_selected(self):
        entry = self.selected()
        if entry is None:
            self.filename.setText(self.t("drop"))
            self.metadata.setText(self.t("drop_hint"))
            self.file_metric.setText("—")
            self.motion_metric.setText("—")
            self.plan.clear()
            self.sampled.clear()
            self.recommended.clear()
            self.strip.kind = None
        else:
            self.filename.setText(entry.path.name)
            self.filename.setToolTip(str(entry.path))
            analysis = entry.analysis
            if analysis is None:
                self.metadata.setText(self.t(entry.state))
                self.file_metric.setText("—")
                self.motion_metric.setText("—")
                self.plan.setText(entry.message[:180])
                self.sampled.clear()
                self.recommended.clear()
                self.strip.kind = None
            else:
                info, cadence = analysis.info, analysis.cadence
                self.metadata.setText(f"{info.width} × {info.height}  ·  {info.codec.upper()}  ·  {info.bit_depth}-bit "
                                      f"{'HDR' if info.is_hdr else 'SDR'}  ·  {float(info.duration):.1f}s")
                self.file_metric.setText(rate_text(info.fps))
                self.motion_metric.setText(f"≈ {analysis.motion_fps:.2f}" if analysis.motion_fps is not None else "—")
                self.plan.setText(self.t({"duplicated": "plan_dup", "native-low": "plan_low", "native-high": "plan_high"}[analysis.kind])
                                  if analysis.motion_fps is not None else self.t("no_motion"))
                self.sampled.setText(self.t("sampled", frames=cadence.frames, unique=cadence.unique,
                                            duplicates=cadence.dup, static=cadence.static))
                self.recommended.setText(self.t("recommended", fps=rate_text(analysis.output_fps)))
                self.strip.kind = analysis.kind if analysis.motion_fps is not None else None
        self.strip.update()
        if entry and entry.result and not (self._worker and self._worker.entry.state in ("analyzing", "converting")):
            result = entry.result
            summary = self.t("output_summary", fps=rate_text(result.fps), frames=result.frames_out)
            if result.verification:
                summary += " · " + self.t("check_summary", duplicates=result.verification.dup_ratio,
                                         length=result.verification.duration_diff * 1000)
            self.progress_label.setText(summary)
            self.progress_label.setToolTip(summary)
        if hasattr(self, "start_button"):
            self.update_controls()

    def build_options(self):
        regions = []
        try:
            for part in self.hud.text().split(";"):
                if part.strip():
                    x, y, width, height = map(int, part.split(","))
                    if min(x, y) < 0 or min(width, height) <= 0:
                        raise ValueError
                    regions.append((x, y, width, height))
        except ValueError:
            raise ValueError(self.t("bad_hud")) from None
        mode = self.rate_mode.currentData()
        fps = self.fps_combo.currentData()
        if fps is not None and fps > 60 and self.preset_combo.currentData() == "youtube":
            raise ValueError(self.t("high_fps_hint"))
        return pipeline.Options(
            fps=fps, dedup=self.dedup.isChecked(), hud_regions=tuple(regions),
            overwrite=self.overwrite_box.isChecked(), verify_output=self.verify_box.isChecked(),
            encode=pipeline.EncodeSettings(codec=self.codec_combo.currentData(), upload_preset=self.preset_combo.currentData(),
                                            quality=self.quality.value() if mode == "quality" else None,
                                            bitrate=round(self.bitrate.value() * 1_000_000) if mode == "bitrate" else None))

    def start_queue(self):
        try:
            self._job_options = self.build_options()
        except (ValueError, RuntimeError) as exc:
            self.set_status("error", str(exc))
            return
        for entry in self.entries:
            if entry.state in ("cancelled", "error"):
                entry.state = "ready" if entry.analysis else "pending"
                self.update_row(entry)
        self._running, self._paused = True, False
        self.update_controls()
        self.pump()

    def pump(self):
        if self._worker or self._paused or self._closing:
            return
        if self._running:
            entry = next((entry for entry in self.entries if entry.state in ("ready", "pending")), None)
        else:
            entry = next((entry for entry in self.entries if entry.state == "pending"), None)
        if entry is None:
            if self._running:
                self._running = False
                if len(self.entries) == 1:
                    item = self.entries[0]
                    self.set_status(item.state, item.message or (self.t("not_verified") if item.state == "complete" else ""))
                else:
                    failed = sum(item.state in ("error", "unsupported") for item in self.entries)
                    self.set_status("warning" if failed else "batch_done", self.t("queue_summary",
                        complete=sum(item.result is not None for item in self.entries), failed=failed,
                        cancelled=sum(item.state == "cancelled" for item in self.entries)))
            self.update_controls()
            return
        mode = "analyze" if entry.state == "pending" else "convert"
        destination = None
        if mode == "convert":
            folder = Path(self.output_dir.text().strip()) if self.output_dir.text().strip() else entry.path.parent
            destination = folder / f"{entry.path.stem}_smooth.mp4"
            same_name = [item for item in self.entries[:self.entries.index(entry)] if item.path.stem == entry.path.stem]
            if same_name:
                destination = folder / f"{entry.path.stem}_{len(same_name) + 1}_smooth.mp4"
            entry.output = destination
        entry.state = "analyzing" if mode == "analyze" else "converting"
        entry.message = ""
        self.update_row(entry)
        self.table.setCurrentCell(self.entries.index(entry), 0)
        worker = Job(entry, mode, destination, self._job_options, self)
        self._worker = worker
        worker.progress.connect(self.on_progress)
        worker.succeeded.connect(self.on_success)
        worker.failed.connect(self.on_failure)
        worker.finished.connect(self.on_finished)
        worker.finished.connect(worker.deleteLater)
        self.update_controls()
        worker.start()

    def on_progress(self, progress):
        if self._paused and self._worker:
            return
        key = "verify_stage" if progress.stage == "verify" else progress.stage
        if key not in TEXT:
            key = "convert"
        self.set_status(key)
        if progress.stage == "convert" and progress.frames_total:
            self.progress_bar.setRange(0, 1000)
            self.progress_bar.setValue(round(progress.frames_out * 1000 / progress.frames_total))
            eta = "—" if progress.remaining is None else f"{round(progress.remaining)}s"
            self.progress_label.setText(self.t("progress", out=progress.frames_out, total=progress.frames_total,
                                              speed=progress.fps, eta=eta))
        elif progress.stage in ("analyze", "model", "mux", "verify"):
            self.progress_bar.setRange(0, 0)
            self.progress_label.clear()

    def on_success(self, result):
        entry = self._worker.entry
        if isinstance(result, pipeline.Analysis):
            self.progress_bar.setRange(0, 1000)
            self.progress_bar.setValue(0)
            entry.analysis = result
            if result.info.is_hdr:
                entry.state, entry.message = "unsupported", self.t("hdr")
                self.set_status("unsupported", entry.message)
            else:
                entry.state = "ready"
                self.set_status("ready")
        else:
            entry.result, entry.output = result, result.output
            report = result.verification
            entry.state = "verified" if report and report.ok else "warning" if report else "complete"
            entry.message = "; ".join(report.problems) if report and not report.ok else ""
            self.set_status(entry.state, entry.message or (self.t("not_verified") if report is None else ""))
            self.progress_bar.setRange(0, 1000)
            self.progress_bar.setValue(1000)
        self.update_row(entry)
        self.show_selected()

    def on_failure(self, message, cancelled):
        entry = self._worker.entry
        entry.state, entry.message = ("cancelled" if cancelled else "error"), message
        if cancelled:
            self._running, self._paused = False, True
        self.update_row(entry)
        self.set_status(entry.state, message)
        self.progress_bar.setRange(0, 1000)
        self.show_selected()

    def on_finished(self):
        self._worker = None
        self.update_controls()
        if self._closing:
            self.close()
        else:
            QTimer.singleShot(0, self.pump)

    def cancel_job(self):
        self._paused, self._running = True, False
        if self._worker:
            self._worker.token.cancel()
            self.set_status("cancelling")
        self.update_controls()

    def remove_selected(self):
        if self._worker or self._running:
            return
        row = self.table.currentRow()
        if row >= 0:
            self.entries.pop(row)
            self.table.removeRow(row)
            self.show_selected()

    def update_controls(self):
        busy = self._running or bool(self._worker and self._worker.mode == "convert")
        for widget in self._setting_widgets:
            widget.setEnabled(not busy)
        self.start_button.setEnabled(not self._running and any(entry.state in ("ready", "error", "cancelled") for entry in self.entries))
        self.cancel_button.setEnabled(self._worker is not None and not self._paused)
        self.remove_button.setEnabled(self._worker is None and not self._running and self.selected() is not None)
        entry = self.selected()
        complete = bool(entry and entry.result and entry.state in ("verified", "complete", "warning"))
        self.open_button.setVisible(complete)
        self.folder_button.setVisible(complete)
        self.details_button.setVisible(bool(entry and (entry.message or entry.result and entry.result.verification)))

    def set_status(self, key, message=""):
        self._status, self._message = key, message
        self.render_status()

    def render_status(self):
        text = self.t(self._status)
        if self._message:
            text += " · " + self._message.splitlines()[0][:180]
        self.status_label.setText(text)
        self.status_label.setToolTip(self._message or text)
        colour = "#FF8E89" if self._status == "error" else "#8DD9AB" if self._status in ("ready", "verified", "complete", "batch_done") else "#F3F1EB"
        self.status_label.setStyleSheet(f"color: {colour};")

    def show_details(self):
        entry = self.selected()
        if entry and (entry.message or entry.result):
            box = QMessageBox(self)
            box.setWindowTitle(self.t("details"))
            box.setText(entry.path.name)
            box.setDetailedText(entry.message or entry.result.verification_text)
            box.exec()

    def open_result(self):
        entry = self.selected()
        if entry and entry.result:
            QDesktopServices.openUrl(QUrl.fromLocalFile(str(entry.result.output)))

    def open_folder(self):
        entry = self.selected()
        if entry and entry.result:
            QDesktopServices.openUrl(QUrl.fromLocalFile(str(entry.result.output.parent)))

    def closeEvent(self, event):
        self._closing = True
        if self._worker:
            self.cancel_job()
            event.ignore()
        else:
            self._save_timer.stop()
            self.save_preferences()
            event.accept()


def main(argv=None):
    parser = argparse.ArgumentParser(prog="framedoubler-gui")
    parser.add_argument("inputs", nargs="*")
    parser.add_argument("--lang", choices=("ko", "en"))
    parser.add_argument("--output-dir")
    args = parser.parse_args(argv)
    app = QApplication.instance() or QApplication([sys.argv[0]])
    app.setApplicationName("FrameDoubler")
    app.setApplicationVersion(__version__)
    window = MainWindow(args.lang, args.output_dir)
    window.show()
    if args.inputs:
        QTimer.singleShot(0, lambda: window.add_paths(args.inputs))
    return app.exec()


if __name__ == "__main__":
    raise SystemExit(main())
