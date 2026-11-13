"""Bounded input prefetching with cancellation and producer error propagation."""

from queue import Empty, Full, Queue
from threading import Event, Thread


class Prefetch:
    def __init__(self, factory, cancel, capacity=2):
        self.factory, self.cancel = factory, cancel
        self.queue = Queue(maxsize=capacity)
        self.stop = Event()
        self.finished = Event()
        self.error = None
        self._started = self._closed = False
        self._thread = Thread(target=self._produce, name="FrameDoubler-input", daemon=True)

    def _produce(self):
        source = None
        try:
            source = iter(self.factory(self.stop))
            for item in source:
                while not self.stop.is_set():
                    try:
                        self.queue.put(item, timeout=0.05)
                        break
                    except Full:
                        pass
                if self.stop.is_set():
                    break
        except BaseException as exc:
            if not self.stop.is_set():
                self.error = exc
        finally:
            try:
                if source is not None and hasattr(source, "close"):
                    source.close()
            except BaseException as exc:
                if not self.stop.is_set() and self.error is None:
                    self.error = exc
            self.finished.set()

    def __iter__(self):
        return self

    def __next__(self):
        if not self._started:
            self._thread.start()
            self._started = True
        while True:
            self.cancel.check()
            if self.error is not None:
                raise self.error
            if self.finished.is_set() and self.queue.empty():
                raise StopIteration
            try:
                return self.queue.get(timeout=0.05)
            except Empty:
                pass

    def close(self):
        if self._closed:
            return
        self._closed = True
        self.stop.set()
        if self._started:
            self._thread.join(timeout=5)
            if self._thread.is_alive():
                raise RuntimeError("The input worker did not stop.")
