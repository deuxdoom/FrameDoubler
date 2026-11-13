"""Cooperative cancellation shared by the pipeline and model downloader."""

from threading import Event


class Cancelled(RuntimeError):
    pass


class CancellationToken:
    def __init__(self):
        self._event = Event()

    def cancel(self) -> None:
        self._event.set()

    def check(self) -> None:
        if self._event.is_set():
            raise Cancelled("Conversion cancelled.")
